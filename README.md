# Git MCP

MCP-сервер с тулами **git_status**, **git_diff**, **git_log**, **git_fetch**, **git_pull**, **git_branch**, **git_show**, **git_submodule**, **git_commit**, **git_push**. Агент видит состояние репозитория и диффы без выхода из чата. Подключается в Cursor (и где угодно) — один exe, stdio.

## Стек

- C#, .NET 10, win-x64, self-contained (как agent-notes-mcp, dotnet-debug-mcp).
- Вызовы `git` через процесс (требуется установленный git в PATH).

## Публикация

```bash
dotnet publish -c Release -o publish
```

Рекомендуется junction: например `D:\git-mcp` → каталог `publish`; в Cursor в mcp.json указать `command`: `D:\git-mcp\GitMcp.exe`, `args`: `[]`.

## Релизы (без Runner)

См. `scripts/publish-release-win.ps1`: мультиплатформа (win-x64, linux-x64, osx-x64), загрузка в GitLab Generic Package и ссылка в релизе. Требуются `GITLAB_URL`, `GITLAB_TOKEN`.

## Тулы

| Имя | Описание | Аргументы |
|-----|----------|-----------|
| `git_status` | Ветка + git status (изменённые/добавленные/удалённые файлы). | `workspace_path` |
| `git_diff` | Дифф (по умолчанию не застейдженный). | `workspace_path`, опционально `path`, `staged` (bool) |
| `git_log` | Последние коммиты (git log -n N --oneline). | `workspace_path`, опционально `n` (по умолчанию 20, макс 500) |
| `git_commit` | Коммит: add (указанные пути или всё) + commit -m. Только по решению пользователя. | `workspace_path`, `message`, опционально `paths` (массив строк) |
| `git_push` | Push в remote. По умолчанию origin и текущая ветка. По решению пользователя. | `workspace_path`, опционально `remote`, `branch` |
| `git_fetch` | Обновить refs с remote (`fetch`, опционально `--all`, `--prune`, имя remote). | `workspace_path`, опционально `remote`, `all`, `prune` |
| `git_pull` | Подтянуть в текущую ветку; по умолчанию `--ff-only`. Без `remote`/`branch` — upstream. | `workspace_path`, опционально `remote`+`branch` вместе, `ff_only` |
| `git_branch` | Список веток (`list`), создание (`create`), удаление (`delete`). | `workspace_path`, `action`, опционально `name`, `start_point`, `force` |
| `git_show` | Показать объект/коммит (`rev`), опционально файл или `--stat`. | `workspace_path`, `rev`, опционально `path`, `stat_only` |
| `git_submodule` | `status` или `update --init` (по умолчанию `--recursive`). | `workspace_path`, опционально `action`, `path`, `recursive` |

`workspace_path` — каталог с репозиторием (корень, где лежит `.git`).
