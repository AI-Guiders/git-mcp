using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GitMcp.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tool = ModelContextProtocol.Protocol.Tool;

// MCP-сервер «Git»: status, diff, log, fetch, pull, branch, show, submodule, commit, push.
// Логика argv — GitMcp.Core (паритет с Cascade IDE, ADR 0019).
// Корень репо: GitWorkTree.GetRepoRoot (поддержка субмодуля: .git — файл).

static string GetRepoRoot(string repoRoot) => GitWorkTree.GetRepoRoot(repoRoot);

static (string output, int exitCode) RunGitRaw(string root, IReadOnlyList<string> args, Encoding? encoding = null)
{
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
    var stdout = stdoutTask.GetAwaiter().GetResult();
    var stderr = stderrTask.GetAwaiter().GetResult();
    var combined = (stdout.TrimEnd() + "\n" + stderr.TrimEnd()).Trim();
    return (combined, p.ExitCode);
}

static string RunGit(string repoRoot, IReadOnlyList<string> args, Encoding? encoding = null)
{
    var root = GetRepoRoot(repoRoot);
    var (output, exitCode) = RunGitRaw(root, args, encoding);
    if (exitCode != 0)
        throw new InvalidOperationException($"git exit {exitCode}: {output}");
    return output;
}

var toolsList = ToolCatalog.Build();

static string GetString(IReadOnlyDictionary<string, JsonElement> args, string key)
    => args.TryGetValue(key, out var v) ? v.GetString() ?? "" : "";

static bool GetBool(IReadOnlyDictionary<string, JsonElement> args, string key)
    => args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;

static bool GetBoolOrDefault(IReadOnlyDictionary<string, JsonElement> args, string key, bool defaultValue)
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

static int GetInt(IReadOnlyDictionary<string, JsonElement> args, string key, int defaultValue)
    => args.TryGetValue(key, out var v) && v.TryGetInt32(out var n) ? n : defaultValue;

static IReadOnlyList<string> GetStringArray(IReadOnlyDictionary<string, JsonElement> args, string key)
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

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "GitMcp", Version = "0.3.2" },
    ProtocolVersion = "2024-11-05",
    Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
    Handlers = new McpServerHandlers
    {
        ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = toolsList }),
        CallToolHandler = (request, cancellationToken) =>
        {
            var name = request.Params?.Name ?? "";
            var args = request.Params?.Arguments is IReadOnlyDictionary<string, JsonElement> a ? a : FrozenDictionary<string, JsonElement>.Empty;
            try
            {
                var workspacePath = GetString(args, "workspace_path");
                if (string.IsNullOrWhiteSpace(workspacePath))
                    throw new ArgumentException("workspace_path is required.");
                string text;
                switch (name)
                {
                    case "git_status":
                    {
                        var parts = new List<string>();
                        foreach (var cmd in GitCommandBuilder.StatusMcpSequence())
                            parts.Add(RunGit(workspacePath, cmd));
                        text = string.Join("\n\n", parts);
                        break;
                    }
                    case "git_diff":
                        var path = GetString(args, "path");
                        var staged = GetBool(args, "staged");
                        text = RunGit(workspacePath, GitCommandBuilder.Diff(staged, path));
                        break;
                    case "git_log":
                        var n = GetInt(args, "n", 20);
                        text = RunGit(workspacePath, GitCommandBuilder.Log(n));
                        break;
                    case "git_commit":
                        var message = GetString(args, "message");
                        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("message is required for git_commit.");
                        var paths = GetStringArray(args, "paths");
                        RunGit(workspacePath, GitCommandBuilder.Add(paths));
                        text = RunGit(workspacePath, GitCommandBuilder.Commit(message));
                        break;
                    case "git_push":
                        var remote = GetString(args, "remote");
                        var branch = GetString(args, "branch");
                        var pushDry = GetBool(args, "dry_run");
                        text = RunGit(workspacePath, GitCommandBuilder.Push(remote, branch, defaultOriginWhenRemoteEmpty: true, pushDry));
                        break;
                    case "git_fetch":
                        var fetchAll = GetBool(args, "all");
                        var fetchPrune = GetBool(args, "prune");
                        var fetchRemote = GetString(args, "remote");
                        var fetchDry = GetBool(args, "dry_run");
                        var fetchR = GitCommandBuilder.Fetch(fetchAll, fetchPrune, fetchRemote, fetchDry);
                        if (!fetchR.IsSuccess)
                            throw new ArgumentException(fetchR.Error);
                        text = RunGit(workspacePath, fetchR.Args!);
                        break;
                    case "git_pull":
                        var pullRem = GetString(args, "remote").Trim();
                        var pullBr = GetString(args, "branch").Trim();
                        var ffOnly = GetBoolOrDefault(args, "ff_only", true);
                        var pullDry = GetBool(args, "dry_run");
                        var pullR = GitCommandBuilder.Pull(pullRem, pullBr, ffOnly, pullDry);
                        if (!pullR.IsSuccess)
                            throw new ArgumentException(pullR.Error);
                        text = RunGit(workspacePath, pullR.Args!);
                        break;
                    case "git_branch":
                        var bAction = GetString(args, "action").Trim();
                        if (string.IsNullOrWhiteSpace(bAction)) bAction = "list";
                        switch (bAction.ToLowerInvariant())
                        {
                            case "list":
                                text = RunGit(workspacePath, GitCommandBuilder.BranchList().Args!);
                                break;
                            case "create":
                                var bn = GetString(args, "name").Trim();
                                var sp = GetString(args, "start_point").Trim();
                                var createR = GitCommandBuilder.BranchCreate(bn, string.IsNullOrWhiteSpace(sp) ? null : sp);
                                if (!createR.IsSuccess)
                                    throw new ArgumentException(createR.Error);
                                text = RunGit(workspacePath, createR.Args!);
                                break;
                            case "delete":
                                var dn = GetString(args, "name").Trim();
                                var force = GetBool(args, "force");
                                var delR = GitCommandBuilder.BranchDelete(dn, force);
                                if (!delR.IsSuccess)
                                    throw new ArgumentException(delR.Error);
                                text = RunGit(workspacePath, delR.Args!);
                                break;
                            default:
                                throw new ArgumentException("git_branch: action must be list, create, or delete.");
                        }
                        break;
                    case "git_show":
                        var rev = GetString(args, "rev").Trim();
                        var showPath = GetString(args, "path");
                        var statOnly = GetBool(args, "stat_only");
                        var showR = GitCommandBuilder.Show(rev, showPath, statOnly);
                        if (!showR.IsSuccess)
                            throw new ArgumentException(showR.Error);
                        text = RunGit(workspacePath, showR.Args!);
                        break;
                    case "git_submodule":
                        var subAction = GetString(args, "action").Trim();
                        if (string.IsNullOrWhiteSpace(subAction)) subAction = "status";
                        switch (subAction.ToLowerInvariant())
                        {
                            case "status":
                                text = RunGit(workspacePath, GitCommandBuilder.SubmoduleStatus().Args!);
                                break;
                            case "update":
                                var rec = GetBoolOrDefault(args, "recursive", true);
                                var subPath = GetString(args, "path").Trim();
                                var subR = GitCommandBuilder.SubmoduleUpdate(rec, string.IsNullOrWhiteSpace(subPath) ? null : subPath);
                                if (!subR.IsSuccess)
                                    throw new ArgumentException(subR.Error);
                                text = RunGit(workspacePath, subR.Args!);
                                break;
                            default:
                                throw new ArgumentException("git_submodule: action must be status or update.");
                        }
                        break;
                    case "git_preflight":
                    {
                        var stagedPreflight = GetBool(args, "staged");
                        var includePatches = GetBoolOrDefault(args, "include_patches", true);

                        var changedOutput = RunGit(workspacePath, GitCommandBuilder.DiffNameOnly(stagedPreflight));
                        var ignoreCrOutput = RunGit(workspacePath, GitCommandBuilder.DiffNameOnly(stagedPreflight, ignoreCrAtEol: true));
                        var ignoreWsOutput = RunGit(workspacePath, GitCommandBuilder.DiffNameOnly(stagedPreflight, ignoreWhitespace: true, ignoreCrAtEol: true));

                        var changed = GitPreflight.ParseNameOnlyOutput(changedOutput);
                        var ignoreCr = GitPreflight.ParseNameOnlyOutput(ignoreCrOutput);
                        var ignoreWs = GitPreflight.ParseNameOnlyOutput(ignoreWsOutput);

                        Dictionary<string, string>? patches = null;
                        if (includePatches && changed.Count > 0)
                        {
                            patches = new Dictionary<string, string>(StringComparer.Ordinal);
                            foreach (var file in changed)
                            {
                                var patchArgs = GitCommandBuilder.DiffPatchForPath(stagedPreflight, file);
                                if (!patchArgs.IsSuccess)
                                    continue;
                                patches[file] = RunGit(workspacePath, patchArgs.Args!);
                            }
                        }

                        var report = GitPreflight.BuildReport(changed, ignoreCr, ignoreWs, patches);
                        text = JsonSerializer.Serialize(new
                        {
                            success = true,
                            staged = stagedPreflight,
                            changed_files = report.ChangedFiles,
                            semantic_files = report.SemanticFiles,
                            whitespace_only_files = report.WhitespaceOnlyFiles,
                            eol_only_files = report.EolOnlyFiles,
                            bom_only_files = report.BomOnlyFiles,
                            suggested_safe_fix_commands = report.SuggestedSafeFixCommands
                        });
                        break;
                    }
                    default: throw new ArgumentException($"Unknown tool: {name}.");
                }
                return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = text }], IsError = false });
            }
            catch (ArgumentException ex) { return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = $"Error: {ex.Message}" }], IsError = true }); }
            catch (Exception ex) { return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "Error: " + ex.Message }], IsError = true }); }
        }
    }
};

var transport = new StdioServerTransport("GitMcp");
await using var server = McpServer.Create(transport, options);
await server.RunAsync();
return 0;
