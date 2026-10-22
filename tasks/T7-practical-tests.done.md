# T7 - practical tests of data and errors

Status: complete.

## Implemented

- `tests/T7/OpenCodeStub.cs` - controlled `opencode.exe`; executes the received SQL against a temporary SQLite database, can delay, spawn a child, and exit while the child lives.
- `tests/T7/T7Tests.cs` - 117 isolated checks for parsing, errors, Retry-After, UTC boundaries, database reads, concurrency, timeout, cancellation, orphan cleanup, and database immutability.
- `tests/T7/Run-T7.ps1` - temporary `XDG_DATA_HOME`, temporary SQLite DB, scoped environment, 120 second watchdog, and environment restore.
- `Check-Local.ps1 -Tests` - build plus T7 checks; `docs/BUILDING.md` documents the command.

## Covered

- `step-finish` with partial tokens/cost, duplicate step, step plus error, several steps.
- Free limit by name, message, body, and mixed case; model-less free limit stays unknown; `429`/`403` are not exhaustion; aborted error.
- Retry-After seconds, HTTP date, case, invalid/fractional/past values, snapshot reset and latest-future reset.
- UTC start and end boundaries with rows exactly at both edges.
- Database-backed reader values, free-limit evidence, exhaustion, concurrency, timeout, cancellation, retry, and unchanged database hash.
- Child cleanup with and without a living parent; Job Object removal makes the orphan test fail.
- Production limits: 60 s refresh and 15 s read timeout.

## Verification

- `.\Check-Local.ps1 -Tests` -> `PASS: local build`, `PASS: 117 T7 checks`.
- Temporary roots only; environment restored after the run; no live OpenCode or user database used.
- Mutation check: disabling Job Object assignment makes the orphan test fail with a live child.
- Five independent review rounds were used; material findings were fixed. The last round found test gaps that were then repaired and verified by the run and mutation check.
