using System.Collections.Frozen;
using System.Text.Json;
using GitMcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tool = ModelContextProtocol.Protocol.Tool;

// MCP-сервер «Git»: status, diff, log, fetch, pull, branch, show, submodule, commit, push.
// Логика argv — GitMcp.Core (паритет с Cascade IDE, ADR 0019). Dispatch — ToolHandlers (CDP-ready).

var toolsList = ToolCatalog.Build();

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "GitMcp", Version = "0.3.3" },
    ProtocolVersion = "2024-11-05",
    Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
    Handlers = new McpServerHandlers
    {
        ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = toolsList }),
        CallToolHandler = (request, cancellationToken) =>
        {
            _ = cancellationToken;
            var name = request.Params?.Name ?? "";
            var args = request.Params?.Arguments is IReadOnlyDictionary<string, JsonElement> a
                ? a
                : FrozenDictionary<string, JsonElement>.Empty;
            try
            {
                var text = ToolHandlers.Handle(name, args);
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = text }],
                    IsError = false
                });
            }
            catch (ArgumentException ex)
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Error: {ex.Message}" }],
                    IsError = true
                });
            }
            catch (Exception ex)
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "Error: " + ex.Message }],
                    IsError = true
                });
            }
        }
    }
};

var transport = new StdioServerTransport("GitMcp");
await using var server = McpServer.Create(transport, options);
await server.RunAsync();
return 0;
