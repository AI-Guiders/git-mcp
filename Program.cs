using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tool = ModelContextProtocol.Protocol.Tool;

// MCP-сервер «Git»: status, diff, log по репозиторию в workspace_path.
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
    ServerInfo = new Implementation { Name = "GitMcp", Version = "0.1.0" },
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
                        var pushArgs = string.IsNullOrWhiteSpace(branch) ? $"push {remote}" : $"push {remote} {branch}";
                        text = RunGit(workspacePath, pushArgs);
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
