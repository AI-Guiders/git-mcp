using System.Text.Json;
using ModelContextProtocol.Protocol;
using Tool = ModelContextProtocol.Protocol.Tool;

/// <summary>Каталог MCP-тулов. Согласован с <c>mcp-tools.manifest.json</c> и <c>docs/MCP-TOOLS.md</c> (генерация: <c>tools/ExportMcpManifest</c>).</summary>
internal static class ToolCatalog
{
    private static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema);

    internal static List<Tool> Build() =>
    [
        new()
        {
            Name = "git_status",
            Description =
                "Состояние репозитория: ветка, изменённые/добавленные/удалённые файлы. workspace_path — корень репо (каталог с .git).",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new { workspace_path = new { type = "string", description = "Каталог workspace (корень репозитория)." } },
                required = new[] { "workspace_path" }
            })
        },
        new()
        {
            Name = "git_diff",
            Description = "Дифф: не застейдженные изменения (или --staged по опции). Опционально path — один файл/путь.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    path = new { type = "string", description = "Опционально: путь к файлу или каталогу для ограничения диффа." },
                    staged = new { type = "boolean", description = "Опционально: true — только staged изменения (git diff --staged)." }
                },
                required = new[] { "workspace_path" }
            })
        },
        new()
        {
            Name = "git_log",
            Description = "Лог коммитов (git log -n N --oneline). По умолчанию последние 20.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    n = new { type = "integer", description = "Опционально: число коммитов (по умолчанию 20)." }
                },
                required = new[] { "workspace_path" }
            })
        },
        new()
        {
            Name = "git_commit",
            Description =
                "Сделать коммит: git add (указанные пути или всё), затем git commit -m. Вызывать только по решению пользователя (логические коммиты).",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    message = new { type = "string", description = "Сообщение коммита." },
                    paths = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "Опционально: пути для add (файлы/каталоги). Если не задано — add -A (все изменения)."
                    }
                },
                required = new[] { "workspace_path", "message" }
            })
        },
        new()
        {
            Name = "git_push",
            Description =
                "Отправить коммиты: git push [remote] [branch]. По умолчанию origin и текущая ветка. Вызывать по решению пользователя.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    remote = new { type = "string", description = "Опционально: remote (по умолчанию origin)." },
                    branch = new { type = "string", description = "Опционально: ветка для push. Если не задано — текущая." }
                },
                required = new[] { "workspace_path" }
            })
        }
    ];
}
