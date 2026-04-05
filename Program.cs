using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tool = ModelContextProtocol.Protocol.Tool;

// MCP-сервер «Git»: status, diff, log, fetch, pull, branch, show, submodule, commit, push.
// Подключается в Cursor (и где угодно) — агент видит состояние репо без выхода из чата.

static string GetRepoRoot(string repoRoot)
{
    var root = Path.GetFullPath(repoRoot.Trim());
    if (File.Exists(root))
        root = Path.GetDirectoryName(root) ?? root;
    if (!Directory.Exists(Path.Combine(root, ".git")))
        throw new ArgumentException($"Not a git repository: {root}");
    return root;
}

static (string output, int exitCode) RunGitRaw(string root, string args, Encoding? encoding = null)
{
    encoding ??= Encoding.UTF8;
    var psi = new ProcessStartInfo
    {
        FileName = "git",
        Arguments = args,
        WorkingDirectory = root,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = encoding,
        StandardErrorEncoding = encoding,
        CreateNoWindow = true,
        UseShellExecute = false
    };
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

static string RunGit(string repoRoot, string args, Encoding? encoding = null)
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

/// <summary>Экранирование одного аргумента для строки <c>git</c> (пробелы, кавычки).</summary>
static string QuoteGitArg(string s)
{
    s = s.Trim();
    if (s.Length == 0)
        return "\"\"";
    s = s.Replace("\"", "\\\"", StringComparison.Ordinal);
    if (s.Contains(' ', StringComparison.Ordinal) || s.Contains('\t', StringComparison.Ordinal))
        return "\"" + s + "\"";
    return s;
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
    ServerInfo = new Implementation { Name = "GitMcp", Version = "0.2.0" },
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
                    case "git_status": text = RunGit(workspacePath, "rev-parse --abbrev-ref HEAD") + "\n\n" + RunGit(workspacePath, "status"); break;
                    case "git_diff":
                        var path = GetString(args, "path");
                        var staged = GetBool(args, "staged");
                        var diffArgs = staged ? "diff --staged" : "diff";
                        if (!string.IsNullOrWhiteSpace(path)) diffArgs += " -- " + path;
                        text = RunGit(workspacePath, diffArgs);
                        break;
                    case "git_log":
                        var n = GetInt(args, "n", 20);
                        if (n <= 0) n = 20;
                        if (n > 500) n = 500;
                        text = RunGit(workspacePath, $"log -n {n} --oneline");
                        break;
                    case "git_commit":
                        var message = GetString(args, "message");
                        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("message is required for git_commit.");
                        var paths = GetStringArray(args, "paths");
                        var addArgs = paths.Count > 0 ? "add -- " + string.Join(" ", paths.Select(p => p.Contains(' ') ? "\"" + p.Replace("\"", "\\\"") + "\"" : p)) : "add -A";
                        RunGit(workspacePath, addArgs);
                        var commitMsgEscaped = "\"" + message.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                        text = RunGit(workspacePath, "commit -m " + commitMsgEscaped);
                        break;
                    case "git_push":
                        var remote = GetString(args, "remote");
                        if (string.IsNullOrWhiteSpace(remote)) remote = "origin";
                        var branch = GetString(args, "branch");
                        var pushArgs = string.IsNullOrWhiteSpace(branch) ? $"push {QuoteGitArg(remote)}" : $"push {QuoteGitArg(remote)} {QuoteGitArg(branch)}";
                        text = RunGit(workspacePath, pushArgs);
                        break;
                    case "git_fetch":
                        var fetchAll = GetBool(args, "all");
                        var fetchPrune = GetBool(args, "prune");
                        var fetchRemote = GetString(args, "remote");
                        if (fetchAll && !string.IsNullOrWhiteSpace(fetchRemote))
                            throw new ArgumentException("git_fetch: do not pass remote when all=true.");
                        var fetchCmd = new StringBuilder("fetch");
                        if (fetchAll)
                        {
                            fetchCmd.Append(" --all");
                            if (fetchPrune) fetchCmd.Append(" --prune");
                        }
                        else
                        {
                            if (fetchPrune) fetchCmd.Append(" --prune");
                            if (!string.IsNullOrWhiteSpace(fetchRemote))
                                fetchCmd.Append(' ').Append(QuoteGitArg(fetchRemote));
                        }
                        text = RunGit(workspacePath, fetchCmd.ToString());
                        break;
                    case "git_pull":
                        var pullRem = GetString(args, "remote").Trim();
                        var pullBr = GetString(args, "branch").Trim();
                        var ffOnly = GetBoolOrDefault(args, "ff_only", true);
                        if (string.IsNullOrWhiteSpace(pullRem) != string.IsNullOrWhiteSpace(pullBr))
                            throw new ArgumentException("git_pull: specify both remote and branch, or neither (pull upstream).");
                        var ffFlag = ffOnly ? "--ff-only" : "";
                        if (string.IsNullOrWhiteSpace(pullRem))
                            text = RunGit(workspacePath, string.IsNullOrEmpty(ffFlag) ? "pull" : $"pull {ffFlag}");
                        else
                            text = RunGit(workspacePath, $"pull {ffFlag} {QuoteGitArg(pullRem)} {QuoteGitArg(pullBr)}".Trim());
                        break;
                    case "git_branch":
                        var bAction = GetString(args, "action").Trim();
                        if (string.IsNullOrWhiteSpace(bAction)) bAction = "list";
                        switch (bAction.ToLowerInvariant())
                        {
                            case "list":
                                text = RunGit(workspacePath, "branch -vv");
                                break;
                            case "create":
                                var bn = GetString(args, "name").Trim();
                                if (string.IsNullOrWhiteSpace(bn)) throw new ArgumentException("git_branch create: name is required.");
                                var sp = GetString(args, "start_point").Trim();
                                text = string.IsNullOrWhiteSpace(sp)
                                    ? RunGit(workspacePath, "branch " + QuoteGitArg(bn))
                                    : RunGit(workspacePath, "branch " + QuoteGitArg(bn) + " " + QuoteGitArg(sp));
                                break;
                            case "delete":
                                var dn = GetString(args, "name").Trim();
                                if (string.IsNullOrWhiteSpace(dn)) throw new ArgumentException("git_branch delete: name is required.");
                                var force = GetBool(args, "force");
                                text = RunGit(workspacePath, "branch " + (force ? "-D " : "-d ") + QuoteGitArg(dn));
                                break;
                            default:
                                throw new ArgumentException("git_branch: action must be list, create, or delete.");
                        }
                        break;
                    case "git_show":
                        var rev = GetString(args, "rev").Trim();
                        if (string.IsNullOrWhiteSpace(rev)) throw new ArgumentException("git_show: rev is required.");
                        var showPath = GetString(args, "path");
                        var statOnly = GetBool(args, "stat_only");
                        if (statOnly)
                            text = RunGit(workspacePath, "show --stat " + QuoteGitArg(rev));
                        else if (!string.IsNullOrWhiteSpace(showPath))
                            text = RunGit(workspacePath, "show " + QuoteGitArg(rev) + " -- " + QuoteGitArg(showPath));
                        else
                            text = RunGit(workspacePath, "show " + QuoteGitArg(rev));
                        break;
                    case "git_submodule":
                        var subAction = GetString(args, "action").Trim();
                        if (string.IsNullOrWhiteSpace(subAction)) subAction = "status";
                        switch (subAction.ToLowerInvariant())
                        {
                            case "status":
                                text = RunGit(workspacePath, "submodule status");
                                break;
                            case "update":
                                var rec = GetBoolOrDefault(args, "recursive", true);
                                var subPath = GetString(args, "path").Trim();
                                var subCmd = new StringBuilder("submodule update --init");
                                if (rec) subCmd.Append(" --recursive");
                                if (!string.IsNullOrWhiteSpace(subPath))
                                    subCmd.Append(" -- ").Append(QuoteGitArg(subPath));
                                text = RunGit(workspacePath, subCmd.ToString());
                                break;
                            default:
                                throw new ArgumentException("git_submodule: action must be status or update.");
                        }
                        break;
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
