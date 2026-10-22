# T8 - regression, build, and smoke check

Status: complete.

## Build and artifacts

- `.\Check-Local.ps1 -Tests` -> `PASS: local build`, `PASS: 117 T7 checks`.
- `Check-Local.ps1` now removes the old `dist/` before compilation.
- Clean `dist/` contains one file: `AIUsageTray.exe`, 122,880 bytes.
- The embedded resource `AIUsageTray.ThirdPartyNotices` is present and equals `THIRD-PARTY-NOTICES.txt`.
- References remain framework-only: `System.Windows.Forms`, `System.Drawing`, `System.Web.Extensions`.
- Static audit found no Node.js process, Worker, listener/server, proxy, or OpenCode HTTP/API call.

## UI regression

- Codex icons at 16/20/24/32 px are byte-identical to the T1 baseline.
- Claude icons at 16/20/24/32 px are byte-identical to the T1 baseline.
- Codex and Claude card sizes match the T1 baseline; rendered labels and rows are unchanged. Only reset dates and countdown values differ because the screenshots were captured at different times.
- OpenCode card was rendered in Russian and English for no-data, normal, exhausted, and unknown states; the card shows local database, estimate, UTC reset, reliability, exhaustion, models, daily tokens, and cost as required by the PRD.
- Runtime smoke checks: 60 s refresh constant, 120 s stale threshold, Esc closes the card, provider switch changes the popup provider, and hover delay is 500 ms. All 11 checks passed.
- Live Codex read returned available data with both windows.
- Live Claude data is unavailable on this machine because no Claude token is installed; the Claude UI regression used the same saved-sign-in fixture as the T1 baseline.

## OpenCode and process checks

- Live OpenCode reader returned 109 observations, 1 free model, token and cost values, with every observation counted once.
- The app made zero TCP connections while running for more than 65 seconds.
- Existing `opencode` PIDs were unchanged before and after the smoke run; their connection counts were unchanged.
- The app remained alive after the 60 s refresh interval and stopped cleanly.
- T7 verifies timeout, cancellation, and orphan cleanup with the Job Object.

## Regression suite

- `tests\Run-Regression.ps1 -Gui` -> 9 provider checks passed, 6 GUI/launch checks passed.
- The suite found a test-harness race: `File.ReadAllLines` held `pids.txt` with `FileShare.Read`, so a child stub could fail to append its PID and the test reported "controlled child did not start". `Pids()` now reads with `FileShare.ReadWrite`; five consecutive non-GUI runs passed.
- Provider checks cover Claude percentage boundaries, Codex/OpenCode timeout, cancellation, orphan cleanup, parallel-read rejection, secret sanitization, and recovery after a timeout.
- GUI checks cover tray registration, provider menu, Claude 0.5% card text, stale retention, provider switch during a pending request, provider disposal, and final-EXE launch/graceful exit.

## Review

- Independent focused review: score 9/10, no material findings.
- Non-blocking notes: a junction named `dist` could point outside the project; a locked EXE makes the clean build stop; the older `dist` Git note is imprecise.
