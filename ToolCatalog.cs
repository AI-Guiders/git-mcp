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
        },
        new()
        {
            Name = "git_fetch",
            Description =
                "git fetch: обновить ссылки remote (refs/remotes). Опционально remote, --all или --prune. Без слияния в рабочее дерево.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    remote = new { type = "string", description = "Опционально: remote (например origin). Не использовать вместе с all=true." },
                    all = new { type = "boolean", description = "Опционально: true — git fetch --all (все remotes)." },
                    prune = new { type = "boolean", description = "Опционально: true — git fetch --prune." }
                },
                required = new[] { "workspace_path" }
            })
        },
        new()
        {
            Name = "git_pull",
            Description =
                "git pull: подтянуть и слить в текущую ветку. По умолчанию --ff-only (без merge-коммита от агента). Явно: remote + branch. Иначе — upstream текущей ветки.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    remote = new { type = "string", description = "Опционально: вместе с branch — git pull remote branch." },
                    branch = new { type = "string", description = "Опционально: вместе с remote." },
                    ff_only = new { type = "boolean", description = "Опционально: по умолчанию true (--ff-only). false — разрешить merge при pull." }
                },
                required = new[] { "workspace_path" }
            })
        },
        new()
        {
            Name = "git_branch",
            Description =
                "Ветки: action=list (ветки -vv), create (git branch name [start_point]), delete (-d или -D при force). По умолчанию list.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    action = new { type = "string", description = "list | create | delete (по умолчанию list)." },
                    name = new { type = "string", description = "Для create/delete: имя ветки." },
                    start_point = new { type = "string", description = "Опционально для create: от какой ревизии/ветки." },
                    force = new { type = "boolean", description = "Для delete: true — git branch -D (принудительно)." }
                },
                required = new[] { "workspace_path" }
            })
        },
        new()
        {
            Name = "git_show",
            Description =
                "git show: содержимое коммита или объекта (rev: HEAD, хеш, ветка~1). Опционально path — файл в этой ревизии; stat_only — только --stat.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    rev = new { type = "string", description = "Ревизия (обязательно): HEAD, sha, main~1, …" },
                    path = new { type = "string", description = "Опционально: путь к файлу внутри ревизии." },
                    stat_only = new { type = "boolean", description = "Опционально: true — только краткая статистика изменений." }
                },
                required = new[] { "workspace_path", "rev" }
            })
        },
        new()
        {
            Name = "git_submodule",
            Description =
                "Субмодули: action=status (git submodule status) или update (git submodule update --init [--recursive], опционально path).",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    action = new { type = "string", description = "status | update (по умолчанию status)." },
                    path = new { type = "string", description = "Опционально для update: только этот субмодуль." },
                    recursive = new { type = "boolean", description = "Опционально для update: по умолчанию true (--recursive)." }
                },
                required = new[] { "workspace_path" }
            })
        }
    ];
}
