using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GitMcp.Core;

namespace GitMcp;

/// <summary>MCP dispatch for git tools — shared by GitMcp host and CDP mount.</summary>
public static class ToolHandlers
{
    public static string Handle(string name, IReadOnlyDictionary<string, JsonElement> args)
    {
        // Multi-root related ship: slices[] — no top-level workspace_path required.
        if (name is "git_commit" or "git_push")
        {
            var slices = TryGetSlices(args);
            if (slices is { Count: > 0 })
            {
                return name == "git_commit"
                    ? HandleCommitSlices(args, slices)
                    : HandlePushSlices(args, slices);
            }
        }

        if (name == "git_plan")
            return HandlePlan(args);

        var workspacePath = GetString(args, "workspace_path");
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("workspace_path is required (or pass slices[] for related multi-root).");

        return name switch
        {
            "git_status" => HandleStatus(workspacePath),
            "git_scene" => HandleScene(workspacePath, args),
            "git_diff_scene" => HandleDiffScene(workspacePath, args),
            "git_diff" => RunGit(workspacePath, GitCommandBuilder.Diff(GetBool(args, "staged"), GetString(args, "path"))),
            "git_log" => RunGit(workspacePath, GitCommandBuilder.Log(GetInt(args, "n", 20))),
            "git_commit" => HandleCommit(workspacePath, args),
            "git_push" => RunGit(workspacePath, GitCommandBuilder.Push(
                GetString(args, "remote"), GetString(args, "branch"), defaultOriginWhenRemoteEmpty: true, GetBool(args, "dry_run"))),
            "git_fetch" => HandleFetch(workspacePath, args),
            "git_pull" => HandlePull(workspacePath, args),
            "git_branch" => HandleBranch(workspacePath, args),
            "git_show" => HandleShow(workspacePath, args),
            "git_submodule" => HandleSubmodule(workspacePath, args),
            "git_preflight" => HandlePreflight(workspacePath, args),
            "git_preflight_fix_safe" => HandlePreflightFixSafe(workspacePath, args),
            _ => throw new ArgumentException($"Unknown tool: {name}.")
        };
    }

    private static string HandleScene(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var includeSubmodules = GetBoolOrDefault(args, "include_submodules", true);
        var probeSubmoduleDirty = GetBoolOrDefault(args, "probe_submodule_dirty", true);
        var maxRoots = Math.Clamp(GetInt(args, "max_roots", GitScene.MaxRootsDefault), 1, 64);
        var maxSubmodules = Math.Clamp(GetInt(args, "max_submodules", GitScene.MaxSubmodulesDefault), 0, 256);

        var rootPaths = new List<string> { workspacePath.Trim() };
        foreach (var extra in GetStringArray(args, "roots"))
        {
            if (rootPaths.Count >= maxRoots)
                break;
            if (!rootPaths.Exists(r => string.Equals(r, extra, StringComparison.OrdinalIgnoreCase)))
                rootPaths.Add(extra);
        }

        var roots = new List<object>();
        foreach (var rootPath in rootPaths.Take(maxRoots))
        {
            try
            {
                roots.Add(BuildSceneRoot(rootPath, includeSubmodules, probeSubmoduleDirty, maxSubmodules));
            }
            catch (Exception ex)
            {
                roots.Add(new
                {
                    path = rootPath,
                    ok = false,
                    error = ex.Message
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            schema = GitScene.SchemaVersion,
            ok = true,
            roots
        });
    }

    private static string HandleDiffScene(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var path = GetString(args, "path").Trim();
        var stagedOnly = GetBool(args, "staged");
        var includeUntracked = GetBoolOrDefault(args, "include_untracked", true);
        var maxFiles = Math.Clamp(GetInt(args, "max_files", GitDiffScene.MaxFilesDefault), 1, 500);
        var maxHunks = Math.Clamp(GetInt(args, "max_hunks", GitDiffScene.MaxHunksDefault), 1, 200);
        var maxHunkLines = Math.Clamp(GetInt(args, "max_hunk_lines", GitDiffScene.MaxHunkLinesDefault), 20, 2000);

        // Detail: one path → structured hunks (not full-repo dump).
        if (!string.IsNullOrWhiteSpace(path))
        {
            var staged = GetBoolOrDefault(args, "staged", false);
            // If staged not forced, prefer unstaged then staged for this path.
            string diffText;
            string area;
            if (stagedOnly || staged)
            {
                diffText = TryRunGit(workspacePath, GitCommandBuilder.Diff(staged: true, path)).Stdout;
                area = "staged";
                if (string.IsNullOrWhiteSpace(diffText))
                {
                    var u = TryRunGit(workspacePath, GitCommandBuilder.Diff(staged: false, path));
                    diffText = u.Stdout;
                    area = "unstaged";
                }
            }
            else
            {
                var u = TryRunGit(workspacePath, GitCommandBuilder.Diff(staged: false, path));
                diffText = u.Stdout;
                area = "unstaged";
                if (string.IsNullOrWhiteSpace(diffText))
                {
                    var s = TryRunGit(workspacePath, GitCommandBuilder.Diff(staged: true, path));
                    diffText = s.Stdout;
                    area = "staged";
                }
            }

            var (hunks, truncated) = GitDiffScene.ParseUnifiedDiff(diffText, maxHunks, maxHunkLines);
            return JsonSerializer.Serialize(new
            {
                schema = GitDiffScene.SchemaVersion,
                ok = true,
                mode = "hunks",
                path,
                area,
                truncated,
                hunk_count = hunks.Count,
                hunks = hunks.Select(h => new
                {
                    header = h.Header,
                    lines = h.Lines.Select(l => new { op = l.Op, text = l.Text })
                })
            });
        }

        // List: file inventory with numstat — agent picks path next.
        var fileRows = new List<GitDiffScene.FileRow>();
        var truncatedFiles = false;

        void AddRows(IReadOnlyList<GitDiffScene.FileRow> rows)
        {
            foreach (var r in rows)
            {
                if (fileRows.Count >= maxFiles)
                {
                    truncatedFiles = true;
                    return;
                }

                fileRows.Add(r);
            }
        }

        if (!stagedOnly)
        {
            var unstaged = TryRunGit(workspacePath, GitCommandBuilder.DiffNumstat(staged: false));
            if (unstaged.ExitCode == 0)
                AddRows(GitDiffScene.ParseNumstat(unstaged.Stdout, "unstaged", maxFiles));
        }

        if (fileRows.Count < maxFiles)
        {
            var stagedNs = TryRunGit(workspacePath, GitCommandBuilder.DiffNumstat(staged: true));
            if (stagedNs.ExitCode == 0)
                AddRows(GitDiffScene.ParseNumstat(stagedNs.Stdout, "staged", maxFiles - fileRows.Count));
        }

        if (includeUntracked && !stagedOnly && fileRows.Count < maxFiles)
        {
            var unt = TryRunGit(workspacePath, GitCommandBuilder.ListUntracked());
            if (unt.ExitCode == 0)
                AddRows(GitDiffScene.ParseUntracked(unt.Stdout, maxFiles - fileRows.Count));
        }

        return JsonSerializer.Serialize(new
        {
            schema = GitDiffScene.SchemaVersion,
            ok = true,
            mode = "list",
            truncated = truncatedFiles,
            file_count = fileRows.Count,
            additions = fileRows.Sum(r => r.Additions),
            deletions = fileRows.Sum(r => r.Deletions),
            hint = "Pass path= to open hunks for one file; prefer over raw git_diff dump.",
            files = fileRows.Select(r => new
            {
                path = r.Path,
                area = r.Area,
                additions = r.Additions,
                deletions = r.Deletions
            })
        });
    }

    private static object BuildSceneRoot(
        string repoPath, bool includeSubmodules, bool probeSubmoduleDirty, int maxSubmodules)
    {
        var root = GitWorkTree.GetRepoRoot(repoPath);
        var branch = TryRunGit(root, GitCommandBuilder.RevParseAbbrevHead()).Stdout.Trim();
        var upstreamR = TryRunGit(root, GitCommandBuilder.RevParseAbbrevUpstream());
        string? upstream = upstreamR.ExitCode == 0 ? upstreamR.Stdout.Trim() : null;
        int? ahead = null;
        int? behind = null;
        if (upstream is not null)
        {
            var ab = TryRunGit(root, GitCommandBuilder.AheadBehindUpstream());
            if (ab.ExitCode == 0)
            {
                var parsed = GitScene.ParseLeftRightCount(ab.Stdout);
                if (parsed is { } p)
                {
                    ahead = p.Ahead;
                    behind = p.Behind;
                }
            }
        }

        var porcelain = TryRunGit(root, GitCommandBuilder.StatusPorcelain());
        var counts = porcelain.ExitCode == 0
            ? GitScene.CountPorcelain(porcelain.Stdout)
            : new GitScene.DirtyCounts(0, 0, 0);

        object[]? submodules = null;
        if (includeSubmodules)
        {
            var subOut = TryRunGit(root, GitCommandBuilder.SubmoduleStatus().Args!);
            if (subOut.ExitCode == 0)
            {
                var entries = GitScene.ParseSubmoduleStatus(subOut.Stdout, maxSubmodules);
                submodules = entries.Select(e =>
                {
                    bool? childDirty = null;
                    int? childAhead = null;
                    int? childBehind = null;
                    string? childBranch = e.BranchHint;
                    if (probeSubmoduleDirty)
                    {
                        var childPath = Path.Combine(root, e.Path.Replace('/', Path.DirectorySeparatorChar));
                        if (Directory.Exists(childPath))
                        {
                            try
                            {
                                var childRoot = GitWorkTree.GetRepoRoot(childPath);
                                if (string.IsNullOrWhiteSpace(childBranch))
                                {
                                    var br = TryRunGit(childRoot, GitCommandBuilder.RevParseAbbrevHead());
                                    if (br.ExitCode == 0)
                                        childBranch = br.Stdout.Trim();
                                }

                                var childPorcelain = TryRunGit(childRoot, GitCommandBuilder.StatusPorcelain());
                                if (childPorcelain.ExitCode == 0)
                                    childDirty = GitScene.CountPorcelain(childPorcelain.Stdout).IsDirty;
                                var up = TryRunGit(childRoot, GitCommandBuilder.RevParseAbbrevUpstream());
                                if (up.ExitCode == 0)
                                {
                                    var ab = TryRunGit(childRoot, GitCommandBuilder.AheadBehindUpstream());
                                    if (ab.ExitCode == 0 && GitScene.ParseLeftRightCount(ab.Stdout) is { } p)
                                    {
                                        childAhead = p.Ahead;
                                        childBehind = p.Behind;
                                    }
                                }
                            }
                            catch
                            {
                                // leave childDirty null
                            }
                        }
                    }

                    var pointerMoved = e.Flag == "+";
                    return (object)new
                    {
                        path = e.Path,
                        sha = e.Sha,
                        flag = e.Flag.Trim().Length == 0 ? " " : e.Flag,
                        branch = childBranch,
                        dirty = childDirty,
                        pointer_moved = pointerMoved,
                        uninitialized = e.Flag == "-",
                        conflict = e.Flag == "U",
                        ahead = childAhead,
                        behind = childBehind
                    };
                }).ToArray();
            }
        }

        return new
        {
            path = root,
            ok = true,
            kind = "repo",
            branch = string.IsNullOrWhiteSpace(branch) ? null : branch,
            upstream,
            dirty = counts.IsDirty,
            counts = new { staged = counts.Staged, unstaged = counts.Unstaged, untracked = counts.Untracked },
            ahead,
            behind,
            submodules
        };
    }

    private static string HandleStatus(string workspacePath)
    {
        var parts = new List<string>();
        foreach (var cmd in GitCommandBuilder.StatusMcpSequence())
            parts.Add(RunGit(workspacePath, cmd));
        return string.Join("\n\n", parts);
    }

    private static string HandleCommit(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var message = GetString(args, "message");
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("message is required for git_commit.");
        var paths = GetStringArray(args, "paths");
        RunGit(workspacePath, GitCommandBuilder.Add(paths.Count > 0 ? paths : null));
        return RunGit(workspacePath, GitCommandBuilder.Commit(message));
    }

    /// <summary>
    /// Related multi-root commit (ADR 0178): one call, per-root paths (no shared-path illusion; no add -A).
    /// </summary>
    private static string HandleCommitSlices(
        IReadOnlyDictionary<string, JsonElement> args,
        IReadOnlyList<CommitSlice> slices)
    {
        var defaultMessage = GetString(args, "message");
        var results = new List<object>();
        var anyOk = false;
        var anyFail = false;
        foreach (var slice in slices.Take(MaxSlices))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(slice.Root))
                    throw new ArgumentException("slice.root is required.");
                if (slice.Paths.Count == 0)
                    throw new ArgumentException(
                        "slice.paths is required and must be non-empty (related mode refuses add -A).");
                var msg = !string.IsNullOrWhiteSpace(slice.Message) ? slice.Message! : defaultMessage;
                if (string.IsNullOrWhiteSpace(msg))
                    throw new ArgumentException("message is required (top-level or slice.message).");

                RunGit(slice.Root, GitCommandBuilder.Add(slice.Paths));
                var output = RunGit(slice.Root, GitCommandBuilder.Commit(msg));
                var sha = TryRunGit(slice.Root, ["rev-parse", "HEAD"]).Stdout.Trim();
                anyOk = true;
                results.Add(new { root = slice.Root, ok = true, paths = slice.Paths, sha = string.IsNullOrWhiteSpace(sha) ? null : sha, output });
            }
            catch (Exception ex)
            {
                anyFail = true;
                results.Add(new { root = slice.Root, ok = false, paths = slice.Paths, error = ex.Message });
            }
        }

        return JsonSerializer.Serialize(new
        {
            schema = "git_commit_slices/v0",
            ok = anyOk && !anyFail,
            partial = anyOk && anyFail,
            slice_count = results.Count,
            slices = results
        });
    }

    private static string HandlePushSlices(
        IReadOnlyDictionary<string, JsonElement> args,
        IReadOnlyList<CommitSlice> slices)
    {
        var defaultRemote = GetString(args, "remote");
        var defaultBranch = GetString(args, "branch");
        var defaultDry = GetBool(args, "dry_run");
        var results = new List<object>();
        var anyOk = false;
        var anyFail = false;
        foreach (var slice in slices.Take(MaxSlices))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(slice.Root))
                    throw new ArgumentException("slice.root is required.");
                var remote = !string.IsNullOrWhiteSpace(slice.Remote) ? slice.Remote : defaultRemote;
                var branch = !string.IsNullOrWhiteSpace(slice.Branch) ? slice.Branch : defaultBranch;
                var dry = slice.DryRun ?? defaultDry;
                var output = RunGit(
                    slice.Root,
                    GitCommandBuilder.Push(remote, branch, defaultOriginWhenRemoteEmpty: true, dry));
                anyOk = true;
                results.Add(new { root = slice.Root, ok = true, dry_run = dry, output });
            }
            catch (Exception ex)
            {
                anyFail = true;
                results.Add(new { root = slice.Root, ok = false, error = ex.Message });
            }
        }

        return JsonSerializer.Serialize(new
        {
            schema = "git_push_slices/v0",
            ok = anyOk && !anyFail,
            partial = anyOk && anyFail,
            slice_count = results.Count,
            slices = results
        });
    }

    private static string HandlePlan(IReadOnlyDictionary<string, JsonElement> args)
    {
        var op = GetString(args, "op").Trim().ToLowerInvariant();
        if (op.Length == 0)
            op = "draft";

        return op switch
        {
            "draft" => HandlePlanDraft(args),
            "validate" => HandlePlanValidate(args),
            "apply" => HandlePlanApply(args),
            _ => throw new ArgumentException("git_plan op must be draft|validate|apply.")
        };
    }

    private static string HandlePlanDraft(IReadOnlyDictionary<string, JsonElement> args)
    {
        var maxPaths = Math.Clamp(GetInt(args, "max_paths", GitPlan.MaxPathsPerRootDefault), 1, 500);
        var maxRoots = Math.Clamp(GetInt(args, "max_roots", GitScene.MaxRootsDefault), 1, 64);
        var rootPaths = new List<string>();
        var primary = GetString(args, "workspace_path").Trim();
        if (primary.Length > 0)
            rootPaths.Add(primary);
        foreach (var extra in GetStringArray(args, "roots"))
        {
            if (rootPaths.Count >= maxRoots)
                break;
            if (!rootPaths.Exists(r => string.Equals(r, extra, StringComparison.OrdinalIgnoreCase)))
                rootPaths.Add(extra);
        }

        if (rootPaths.Count == 0)
            throw new ArgumentException("git_plan draft needs workspace_path and/or roots[].");

        var roots = new List<object>();
        foreach (var root in rootPaths.Take(maxRoots))
        {
            try
            {
                var porcelain = TryRunGit(root, GitCommandBuilder.StatusPorcelain());
                if (porcelain.ExitCode != 0)
                    throw new InvalidOperationException(porcelain.Stderr.Trim());
                var paths = GitPlan.ParsePorcelainPaths(porcelain.Stdout, maxPaths);
                var counts = GitScene.CountPorcelain(porcelain.Stdout);
                roots.Add(new
                {
                    path = root,
                    ok = true,
                    dirty = counts.IsDirty,
                    counts = new { staged = counts.Staged, unstaged = counts.Unstaged, untracked = counts.Untracked },
                    paths,
                    path_count = paths.Count,
                    truncated = paths.Count >= maxPaths && counts.IsDirty
                });
            }
            catch (Exception ex)
            {
                roots.Add(new { path = root, ok = false, error = ex.Message });
            }
        }

        return JsonSerializer.Serialize(new
        {
            schema = GitPlan.SchemaVersion,
            op = "draft",
            ok = true,
            hint = "Split dirty paths into slices[{root,paths[],message}]; then op=validate or op=apply. Optional push=true on apply.",
            roots
        });
    }

    private static string HandlePlanValidate(IReadOnlyDictionary<string, JsonElement> args)
    {
        var slices = TryGetSlices(args)
            ?? throw new ArgumentException("git_plan validate requires slices=[{root,paths,message}].");
        var checkDirty = GetBoolOrDefault(args, "check_dirty", true);
        var results = new List<object>();
        var anyFail = false;
        foreach (var slice in slices.Take(MaxSlices))
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(slice.Root))
                errors.Add("root required");
            if (slice.Paths.Count == 0)
                errors.Add("paths required (non-empty)");
            if (string.IsNullOrWhiteSpace(slice.Message))
                errors.Add("message required");

            if (checkDirty && errors.Count == 0)
            {
                try
                {
                    var porcelain = TryRunGit(slice.Root, GitCommandBuilder.StatusPorcelain());
                    var dirty = new HashSet<string>(
                        GitPlan.ParsePorcelainPaths(porcelain.Stdout, 2000),
                        StringComparer.Ordinal);
                    foreach (var p in slice.Paths)
                    {
                        if (!dirty.Contains(p))
                            errors.Add($"path_not_dirty:{p}");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                }
            }

            var ok = errors.Count == 0;
            if (!ok)
                anyFail = true;
            results.Add(new
            {
                root = slice.Root,
                ok,
                path_count = slice.Paths.Count,
                message_preview = slice.Message is { Length: > 0 } m
                    ? (m.Length <= 80 ? m : m[..80] + "…")
                    : null,
                errors = ok ? Array.Empty<string>() : errors.ToArray()
            });
        }

        return JsonSerializer.Serialize(new
        {
            schema = GitPlan.SchemaVersion,
            op = "validate",
            ok = !anyFail,
            slice_count = results.Count,
            slices = results,
            next = anyFail ? "fix slices then validate again" : "op=apply (optional push=true)"
        });
    }

    private static string HandlePlanApply(IReadOnlyDictionary<string, JsonElement> args)
    {
        var slices = TryGetSlices(args)
            ?? throw new ArgumentException("git_plan apply requires slices=[{root,paths,message}].");
        // Reuse validate gate unless skip_validate=true
        if (!GetBool(args, "skip_validate"))
        {
            var validation = HandlePlanValidate(args);
            using var vdoc = JsonDocument.Parse(validation);
            if (!vdoc.RootElement.GetProperty("ok").GetBoolean())
            {
                return JsonSerializer.Serialize(new
                {
                    schema = GitPlan.SchemaVersion,
                    op = "apply",
                    ok = false,
                    error = "validate_failed",
                    validation = JsonSerializer.Deserialize<object>(validation)
                });
            }
        }

        var commitJson = HandleCommitSlices(args, slices);
        using var cdoc = JsonDocument.Parse(commitJson);
        object? pushResult = null;
        if (GetBool(args, "push"))
        {
            var pushSlices = new List<CommitSlice>();
            foreach (var s in cdoc.RootElement.GetProperty("slices").EnumerateArray())
            {
                if (s.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True
                    && s.TryGetProperty("root", out var rootEl))
                {
                    var root = rootEl.GetString() ?? "";
                    if (root.Length > 0)
                        pushSlices.Add(new CommitSlice(root, [], null, null, null, null));
                }
            }

            if (pushSlices.Count > 0)
                pushResult = JsonSerializer.Deserialize<object>(HandlePushSlices(args, pushSlices));
        }

        return JsonSerializer.Serialize(new
        {
            schema = GitPlan.SchemaVersion,
            op = "apply",
            ok = cdoc.RootElement.GetProperty("ok").GetBoolean(),
            partial = cdoc.RootElement.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True,
            commit = JsonSerializer.Deserialize<object>(commitJson),
            push = pushResult
        });
    }

    private const int MaxSlices = 16;

    private readonly record struct CommitSlice(
        string Root,
        IReadOnlyList<string> Paths,
        string? Message,
        string? Remote,
        string? Branch,
        bool? DryRun);

    private static IReadOnlyList<CommitSlice>? TryGetSlices(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("slices", out var el) || el.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<CommitSlice>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var root = item.TryGetProperty("root", out var r) ? r.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(root) && item.TryGetProperty("workspace_path", out var wp))
                root = wp.GetString() ?? "";
            var paths = new List<string>();
            if (item.TryGetProperty("paths", out var pEl) && pEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pEl.EnumerateArray())
                {
                    if (p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s)
                        paths.Add(s);
                }
            }

            string? message = item.TryGetProperty("message", out var m) ? m.GetString() : null;
            string? remote = item.TryGetProperty("remote", out var rem) ? rem.GetString() : null;
            string? branch = item.TryGetProperty("branch", out var br) ? br.GetString() : null;
            bool? dryRun = null;
            if (item.TryGetProperty("dry_run", out var d))
            {
                dryRun = d.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
            }

            list.Add(new CommitSlice(root.Trim(), paths, message, remote, branch, dryRun));
        }

        return list.Count == 0 ? null : list;
    }

    private static string HandleFetch(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var fetchR = GitCommandBuilder.Fetch(
            GetBool(args, "all"), GetBool(args, "prune"), GetString(args, "remote"), GetBool(args, "dry_run"));
        if (!fetchR.IsSuccess)
            throw new ArgumentException(fetchR.Error);
        return RunGit(workspacePath, fetchR.Args!);
    }

    private static string HandlePull(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var pullR = GitCommandBuilder.Pull(
            GetString(args, "remote").Trim(),
            GetString(args, "branch").Trim(),
            GetBoolOrDefault(args, "ff_only", true),
            GetBool(args, "dry_run"));
        if (!pullR.IsSuccess)
            throw new ArgumentException(pullR.Error);
        return RunGit(workspacePath, pullR.Args!);
    }

    private static string HandleBranch(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var bAction = GetString(args, "action").Trim();
        if (string.IsNullOrWhiteSpace(bAction))
            bAction = "list";
        return bAction.ToLowerInvariant() switch
        {
            "list" => RunGit(workspacePath, GitCommandBuilder.BranchList().Args!),
            "create" => BranchCreate(workspacePath, args),
            "delete" => BranchDelete(workspacePath, args),
            _ => throw new ArgumentException("git_branch: action must be list, create, or delete.")
        };
    }

    private static string BranchCreate(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var bn = GetString(args, "name").Trim();
        var sp = GetString(args, "start_point").Trim();
        var createR = GitCommandBuilder.BranchCreate(bn, string.IsNullOrWhiteSpace(sp) ? null : sp);
        if (!createR.IsSuccess)
            throw new ArgumentException(createR.Error);
        return RunGit(workspacePath, createR.Args!);
    }

    private static string BranchDelete(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var delR = GitCommandBuilder.BranchDelete(GetString(args, "name").Trim(), GetBool(args, "force"));
        if (!delR.IsSuccess)
            throw new ArgumentException(delR.Error);
        return RunGit(workspacePath, delR.Args!);
    }

    private static string HandleShow(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var showR = GitCommandBuilder.Show(
            GetString(args, "rev").Trim(), GetString(args, "path"), GetBool(args, "stat_only"));
        if (!showR.IsSuccess)
            throw new ArgumentException(showR.Error);
        return RunGit(workspacePath, showR.Args!);
    }

    private static string HandleSubmodule(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var subAction = GetString(args, "action").Trim();
        if (string.IsNullOrWhiteSpace(subAction))
            subAction = "status";
        return subAction.ToLowerInvariant() switch
        {
            "status" => RunGit(workspacePath, GitCommandBuilder.SubmoduleStatus().Args!),
            "update" => SubmoduleUpdate(workspacePath, args),
            _ => throw new ArgumentException("git_submodule: action must be status or update.")
        };
    }

    private static string SubmoduleUpdate(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var subPath = GetString(args, "path").Trim();
        var subR = GitCommandBuilder.SubmoduleUpdate(
            GetBoolOrDefault(args, "recursive", true),
            string.IsNullOrWhiteSpace(subPath) ? null : subPath);
        if (!subR.IsSuccess)
            throw new ArgumentException(subR.Error);
        return RunGit(workspacePath, subR.Args!);
    }

    private static string HandlePreflight(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var stagedPreflight = GetBool(args, "staged");
        var includeUntracked = GetBoolOrDefault(args, "include_untracked", true);
        var includePatches = GetBoolOrDefault(args, "include_patches", true);

        var changedOutput = RunGit(workspacePath, GitCommandBuilder.DiffNameOnly(stagedPreflight));
        var ignoreCrOutput = RunGit(workspacePath, GitCommandBuilder.DiffNameOnly(stagedPreflight, ignoreCrAtEol: true));
        var ignoreWsOutput = RunGit(workspacePath, GitCommandBuilder.DiffNameOnly(stagedPreflight, ignoreWhitespace: true, ignoreCrAtEol: true));

        var changed = GitPreflight.ParseNameOnlyOutput(changedOutput);
        var untracked = includeUntracked
            ? GitPreflight.ParseNameOnlyOutput(RunGit(workspacePath, GitCommandBuilder.ListUntracked()))
            : [];
        var ignoreCr = GitPreflight.ParseNameOnlyOutput(ignoreCrOutput);
        var ignoreWs = GitPreflight.ParseNameOnlyOutput(ignoreWsOutput);
        var patches = BuildPatches(workspacePath, changed, stagedPreflight, includePatches);
        var report = GitPreflight.BuildReport(changed, untracked, ignoreCr, ignoreWs, patches);
        return JsonSerializer.Serialize(new
        {
            success = true,
            staged = stagedPreflight,
            changed_files = report.ChangedFiles,
            untracked_files = report.UntrackedFiles,
            semantic_files = report.SemanticFiles,
            whitespace_only_files = report.WhitespaceOnlyFiles,
            eol_only_files = report.EolOnlyFiles,
            bom_only_files = report.BomOnlyFiles,
            suggested_safe_fix_commands = report.SuggestedSafeFixCommands
        });
    }

    private static string HandlePreflightFixSafe(string workspacePath, IReadOnlyDictionary<string, JsonElement> args)
    {
        var includePatches = GetBoolOrDefault(args, "include_patches", true);
        RunGit(workspacePath, GitCommandBuilder.AddRenormalize());

        var changed = GitPreflight.ParseNameOnlyOutput(RunGit(workspacePath, GitCommandBuilder.DiffNameOnly(staged: false)));
        var untracked = GitPreflight.ParseNameOnlyOutput(RunGit(workspacePath, GitCommandBuilder.ListUntracked()));
        var ignoreCr = GitPreflight.ParseNameOnlyOutput(RunGit(workspacePath, GitCommandBuilder.DiffNameOnly(staged: false, ignoreCrAtEol: true)));
        var ignoreWs = GitPreflight.ParseNameOnlyOutput(
            RunGit(workspacePath, GitCommandBuilder.DiffNameOnly(staged: false, ignoreWhitespace: true, ignoreCrAtEol: true)));
        var patches = BuildPatches(workspacePath, changed, staged: false, includePatches);
        var report = GitPreflight.BuildReport(changed, untracked, ignoreCr, ignoreWs, patches);
        return JsonSerializer.Serialize(new
        {
            success = true,
            applied = new[] { "git add --renormalize ." },
            changed_files = report.ChangedFiles,
            untracked_files = report.UntrackedFiles,
            semantic_files = report.SemanticFiles,
            whitespace_only_files = report.WhitespaceOnlyFiles,
            eol_only_files = report.EolOnlyFiles,
            bom_only_files = report.BomOnlyFiles,
            suggested_safe_fix_commands = report.SuggestedSafeFixCommands
        });
    }

    private static Dictionary<string, string>? BuildPatches(
        string workspacePath, IReadOnlyList<string> changed, bool staged, bool includePatches)
    {
        if (!includePatches || changed.Count == 0)
            return null;
        var patches = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in changed)
        {
            var patchArgs = GitCommandBuilder.DiffPatchForPath(staged, file);
            if (!patchArgs.IsSuccess)
                continue;
            patches[file] = RunGit(workspacePath, patchArgs.Args!);
        }
        return patches;
    }

    private readonly record struct GitRunResult(int ExitCode, string Stdout, string Stderr);

    private static GitRunResult TryRunGit(string repoRoot, IReadOnlyList<string> args, Encoding? encoding = null)
    {
        var root = GitWorkTree.GetRepoRoot(repoRoot);
        encoding ??= Encoding.UTF8;
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p == null)
            throw new InvalidOperationException("Failed to start git.");
        p.StandardInput.Close();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromSeconds(15)))
        {
            p.Kill(entireProcessTree: true);
            throw new InvalidOperationException("git timed out after 15s.");
        }

        return new GitRunResult(p.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static string RunGit(string repoRoot, IReadOnlyList<string> args, Encoding? encoding = null)
    {
        var r = TryRunGit(repoRoot, args, encoding);
        var combined = (r.Stdout.TrimEnd() + "\n" + r.Stderr.TrimEnd()).Trim();
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"git exit {r.ExitCode}: {combined}");
        return combined;
    }

    private static string GetString(IReadOnlyDictionary<string, JsonElement> args, string key)
        => args.TryGetValue(key, out var v) ? v.GetString() ?? "" : "";

    private static bool GetBool(IReadOnlyDictionary<string, JsonElement> args, string key)
        => args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool GetBoolOrDefault(IReadOnlyDictionary<string, JsonElement> args, string key, bool defaultValue)
    {
        if (!args.TryGetValue(key, out var v))
            return defaultValue;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue
        };
    }

    private static int GetInt(IReadOnlyDictionary<string, JsonElement> args, string key, int defaultValue)
        => args.TryGetValue(key, out var v) && v.TryGetInt32(out var n) ? n : defaultValue;

    private static IReadOnlyList<string> GetStringArray(IReadOnlyDictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var e in v.EnumerateArray())
        {
            var s = e.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s.Trim());
        }
        return list;
    }
}
