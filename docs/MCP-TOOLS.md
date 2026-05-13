# Git MCP — каталог тулов

<!-- GENERATED:ToolCatalog START -->

> Автогенерация из `ToolCatalog.Build()`. Не править этот блок вручную.
>
> Обновление: из каталога `git-mcp` выполнить `dotnet run --project tools/ExportMcpManifest -- --write`.
>
> Тексты совпадают с полем `description` у инструментов MCP; полная схема — в `inputSchema`.

### `git_status`

Состояние репозитория: ветка, изменённые/добавленные/удалённые файлы. workspace_path — корень репо (каталог .git или файл .git у субмодуля).

### `git_diff`

Дифф: не застейдженные изменения (или --staged по опции). Опционально path — один файл/путь.

### `git_log`

Лог коммитов (git log -n N --oneline). По умолчанию последние 20.

### `git_commit`

Сделать коммит: git add (указанные пути или всё), затем git commit -m. Вызывать только по решению пользователя (логические коммиты).

### `git_push`

Отправить коммиты: git push [remote] [branch]. По умолчанию origin и текущая ветка. dry_run=true — git push --dry-run (без отправки). Вызывать по решению пользователя.

### `git_fetch`

git fetch: обновить ссылки remote (refs/remotes). Опционально remote, --all или --prune. dry_run=true — git fetch --dry-run. Без слияния в рабочее дерево.

### `git_pull`

git pull: подтянуть и слить в текущую ветку. По умолчанию --ff-only (без merge-коммита от агента). dry_run=true — git pull --dry-run (Git 2.27+). Явно: remote + branch. Иначе — upstream текущей ветки.

### `git_branch`

Ветки: action=list (ветки -vv), create (git branch name [start_point]), delete (-d или -D при force). По умолчанию list.

### `git_show`

git show: содержимое коммита или объекта (rev: HEAD, хеш, ветка~1). Опционально path — файл в этой ревизии; stat_only — только --stat.

### `git_submodule`

Субмодули: action=status (git submodule status) или update (git submodule update --init [--recursive], опционально path).

### `git_preflight`

Preflight перед коммитом: классифицирует изменения на semantic/whitespace-only/eol-only/bom-only и возвращает safe-fix подсказки.

### `git_preflight_fix_safe`

Применить безопасные preflight-фиксы: git add --renormalize . и вернуть обновлённый preflight-отчёт.

<!-- GENERATED:ToolCatalog END -->

