# Git MCP — каталог тулов

<!-- GENERATED:ToolCatalog START -->

> Автогенерация из `ToolCatalog.Build()`. Не править этот блок вручную.
>
> Обновление: из каталога `git-mcp` выполнить `dotnet run --project tools/ExportMcpManifest -- --write`.
>
> Тексты совпадают с полем `description` у инструментов MCP; полная схема — в `inputSchema`.

### `git_status`

Состояние репозитория: ветка, изменённые/добавленные/удалённые файлы. workspace_path — корень репо (каталог с .git).

### `git_diff`

Дифф: не застейдженные изменения (или --staged по опции). Опционально path — один файл/путь.

### `git_log`

Лог коммитов (git log -n N --oneline). По умолчанию последние 20.

### `git_commit`

Сделать коммит: git add (указанные пути или всё), затем git commit -m. Вызывать только по решению пользователя (логические коммиты).

### `git_push`

Отправить коммиты: git push [remote] [branch]. По умолчанию origin и текущая ветка. Вызывать по решению пользователя.

<!-- GENERATED:ToolCatalog END -->

