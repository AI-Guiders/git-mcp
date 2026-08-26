using McpToolManifest;
using GitMcp;

//   JSON stdout:  dotnet run --project tools/ExportMcpManifest
//   MD stdout:    dotnet run --project tools/ExportMcpManifest -- --md-only
//   write files:  dotnet run --project tools/ExportMcpManifest -- --write

var tools = ToolCatalog.Build().Select(t => (t.Name!, (string?)t.Description)).ToList();
return McpToolManifestExporter.Run(
    args,
    tools,
    new McpToolManifestExportOptions
    {
        McpId = "git-mcp",
        Title = "Git MCP",
        RepoFolderName = "git-mcp",
        SchemaHint = "Тексты совпадают с полем `description` у инструментов MCP; полная схема — в `inputSchema`.",
    });
