# T6 — Лицензии, README и происхождение — выполнено

## Что сделано
- `THIRD-PARTY-NOTICES.txt`: полный текст MIT и `Copyright (c) 2026 TheLabCorner` для `thelabcorner/opencode-zen-fut-api`, commit `fda6dddd138ad382849e1a2b76c608db231d9942`.
- В README RU/EN добавлены ссылка, описание адаптированных идей (наблюдения, UTC-окно, диапазон/достоверность, Retry-After, прогноз, группировка моделей), отказ от Node.js/Worker/CLI/SQLite-ридера внешнего проекта и указание, что официального API лимитов Zen нет.
- README RU/EN описывают фактическое поведение: постоянный знак OpenCode без цифр, квоту, остаток, процент, диапазон, сброс, достоверность, исчерпание, модели с токенами; токены и стоимость — второстепенные строки за UTC-сутки.
- Удалены `planned`/`Планируемый` и старые описания значка OpenCode с цифрами.
- Указано, что имя и знак OpenCode используются только для обозначения провайдера.
- `docs/reference-projects.md` содержит закреплённый commit и помечен как локальный файл, игнорируемый Git.

## Проверка
- `.\Check-Local.ps1` — PASS.
- Ресурс `AIUsageTray.ThirdPartyNotices` присутствует в EXE; внутри есть MIT и полный commit.
- `git check-ignore` подтверждает, что `docs/reference-projects.md` игнорируется.
- Пример карточки сверен с T5: порядок строк, `(UTC)`, локальное время, countdown, прогноз до сброса.
- README Codex/Claude не менялись по смыслу.

## Ревью
- Два независимых review: 7/10, затем 8/10. Все material findings исправлены: невозможный пример сброса/прогноза и пропуск Retry-After в списке идей.

## Файлы
- `README.md`
- `README.ru.md`
- `THIRD-PARTY-NOTICES.txt`
- `docs/reference-projects.md` (локальный, не в Git)
