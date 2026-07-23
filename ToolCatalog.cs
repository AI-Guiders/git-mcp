using System.Text.Json;
using ModelContextProtocol.Protocol;
using Tool = ModelContextProtocol.Protocol.Tool;

namespace GitMcp;

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
                "Состояние репозитория: ветка, изменённые/добавленные/удалённые файлы. workspace_path — корень репо (каталог .git или файл .git у субмодуля).",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new { workspace_path = new { type = "string", description = "Каталог workspace (корень репозитория)." } },
                required = new[] { "workspace_path" }
            })
        },
        new()
        {
            Name = "git_scene",
            Description =
                "SCM scene (compact): dirty counts, ahead/behind, submodule map — без полного porcelain. Prefer before git_status dump. Optional roots[] for multi-repo; include_submodules (default true).",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Primary repo root." },
                    roots = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "Optional extra repo roots (multi-root / sibling workspaces)."
                    },
                    include_submodules = new { type = "boolean", description = "Default true — include submodule map." },
                    probe_submodule_dirty = new { type = "boolean", description = "Default true — probe each submodule dirty/ahead (slower)." },
                    max_roots = new { type = "integer", description = "Cap roots (default 16)." },
                    max_submodules = new { type = "integer", description = "Cap submodule entries per root (default 64)." }
                },
                required = new[] { "workspace_path" }
            })
        },
        new()
        {
            Name = "git_diff_scene",
            Description =
                "Diff scene (agent comfort): list mode = files+numstat (no dump); path= → structured hunks for one file. Prefer before raw git_diff.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Repo root." },
                    path = new { type = "string", description = "Optional: one file → hunks mode." },
                    staged = new { type = "boolean", description = "List/hunks: prefer staged (default false = unstaged first)." },
                    include_untracked = new { type = "boolean", description = "List mode: include untracked (default true)." },
                    max_files = new { type = "integer", description = "List cap (default 80)." },
                    max_hunks = new { type = "integer", description = "Hunks cap (default 40)." },
                    max_hunk_lines = new { type = "integer", description = "Lines per hunk cap (default 200)." }
                },
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
                "Commit: add paths then commit -m. Single-root: workspace_path (+ optional paths). Related multi-root: slices=[{root,paths,message?}]. slices.paths required (no add -A). Operator intent only.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Single-root repo. Optional after cdp_open (scm_root) or when slices set." },
                    message = new { type = "string", description = "Commit message (default for slices missing slice.message)." },
                    paths = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "Single-root: paths to add. If omitted — add -A. Ignored when slices set."
                    },
                    slices = new
                    {
                        type = "array",
                        description = "Related multi-root: [{root, paths[], message?}]. Prefer over N× commit. paths required per slice.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                root = new { type = "string" },
                                paths = new { type = "array", items = new { type = "string" } },
                                message = new { type = "string" }
                            }
                        }
                    }
                },
                required = new[] { "message" }
            })
        },
        new()
        {
            Name = "git_push",
            Description =
                "Push [remote] [branch]. Single-root: workspace_path. Related multi-root: slices=[{root,remote?,branch?,dry_run?}]. Operator intent only.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Single-root. Optional after cdp_open or when slices set." },
                    remote = new { type = "string", description = "Default remote (origin)." },
                    branch = new { type = "string", description = "Default branch (current)." },
                    dry_run = new { type = "boolean", description = "true — push --dry-run." },
                    slices = new
                    {
                        type = "array",
                        description = "Related multi-root: [{root, remote?, branch?, dry_run?}].",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                root = new { type = "string" },
                                remote = new { type = "string" },
                                branch = new { type = "string" },
                                dry_run = new { type = "boolean" }
                            }
                        }
                    }
                },
                required = Array.Empty<string>()
            })
        },
        new()
        {
            Name = "git_fetch",
            Description =
                "git fetch: обновить ссылки remote (refs/remotes). Опционально remote, --all или --prune. dry_run=true — git fetch --dry-run. Без слияния в рабочее дерево.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    remote = new { type = "string", description = "Опционально: remote (например origin). Не использовать вместе с all=true." },
                    all = new { type = "boolean", description = "Опционально: true — git fetch --all (все remotes)." },
                    prune = new { type = "boolean", description = "Опционально: true — git fetch --prune." },
                    dry_run = new { type = "boolean", description = "Опционально: true — только предпросмотр (--dry-run)." }
                },
                required = new[] { "workspace_path" }
            })
        },
        new()
        {
            Name = "git_pull",
            Description =
                "git pull: подтянуть и слить в текущую ветку. По умолчанию --ff-only (без merge-коммита от агента). dry_run=true — git pull --dry-run (Git 2.27+). Явно: remote + branch. Иначе — upstream текущей ветки.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    remote = new { type = "string", description = "Опционально: вместе с branch — git pull remote branch." },
                    branch = new { type = "string", description = "Опционально: вместе с remote." },
                    ff_only = new { type = "boolean", description = "Опционально: по умолчанию true (--ff-only). false — разрешить merge при pull." },
                    dry_run = new { type = "boolean", description = "Опционально: true — только предпросмотр (--dry-run)." }
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
        },
        new()
        {
            Name = "git_preflight",
            Description =
                "Preflight перед коммитом: классифицирует изменения на semantic/whitespace-only/eol-only/bom-only и возвращает safe-fix подсказки.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    staged = new { type = "boolean", description = "Опционально: true — анализировать staged изменения." },
                    include_untracked = new { type = "boolean", description = "Опционально: true (по умолчанию) — включать untracked_files." },
                    include_patches = new { type = "boolean", description = "Опционально: true — включить эвристику BOM-only по патчам (медленнее)." }
                },
                required = new[] { "workspace_path" }
            })
        },
        new()
        {
            Name = "git_preflight_fix_safe",
            Description =
                "Применить безопасные preflight-фиксы: git add --renormalize . и вернуть обновлённый preflight-отчёт.",
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    workspace_path = new { type = "string", description = "Корень репозитория." },
                    include_patches = new { type = "boolean", description = "Опционально: true — включить эвристику BOM-only по патчам после фикса." }
                },
                required = new[] { "workspace_path" }
            })
        }
    ];
}
