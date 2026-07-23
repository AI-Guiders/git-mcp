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
        var workspacePath = GetString(args, "workspace_path");
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("workspace_path is required.");

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
        RunGit(workspacePath, GitCommandBuilder.Add(GetStringArray(args, "paths")));
        return RunGit(workspacePath, GitCommandBuilder.Commit(message));
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
