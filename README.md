# AI Usage Tray

[English](README.md) | [Русский](README.ru.md)

A compact Windows tray utility that shows your remaining Codex and Claude limits and your OpenCode token and cost usage for today. Each launch adds its own tray icon, and you choose the provider from the tray menu. Hover over an icon to see more details.

Run the utility from a single file, <code>AIUsageTray.exe</code>. It uses the clients you already have installed and signed in.

## Features

- Two rows on each icon. For Codex and Claude the five-hour limit is on top and the weekly limit is below; for OpenCode tokens today are on top and the estimated cost is below.
- For Codex and Claude, each row has its own color based on the remaining limit. The OpenCode rows are usage, not limits, and stay neutral.
- The plan in the card title, <code>&lt;Provider&gt; · PLAN</code>, is shown in uppercase and bold.
- Manual provider selection from the tray menu: detected providers are bright and selectable, undetected providers are gray.
- The card opens after 500 ms of continuous hovering; moving the pointer away before it appears cancels the opening.
- The number of available manual limit resets and the expiry dates of returned reset entries (Codex).
- The subscription expiry date, when it is available in Codex's saved metadata.
- The Windows system font and light or dark theme, with a width that fits the content.
- Russian and English UI with automatic language selection.
- Data refreshes every 60 seconds. The card marks data as out of date when a request fails or the data gets old.

## Requirements

- 64-bit Windows. The current build has been checked on Windows 11.
- .NET Framework 4.8.1.
- At least one supported client, installed and signed in:
  - **Codex** with the native <code>codex.exe</code> that supports <code>app-server</code>.
  - **Claude** — Claude Code or Claude Desktop, signed in. Claude Code is the primary token source; the Claude Desktop token is an encrypted blob and may not yield a usable access token.
  - **OpenCode** with the CLI and its local database.
- Codex and Claude must be able to reach their service; OpenCode reads its local database.

The utility detects each client through <code>PATH</code>, its install location, or its saved credentials. The client's own window can be closed.

## Run

The current public release is <a href="https://github.com/btc072/AI-Usage-Tray/releases/tag/v2.0.0">v2.0.0</a>, which supports Codex, Claude, and OpenCode. The earlier <a href="https://github.com/btc072/AI-Usage-Tray/releases/tag/v1.0.0">v1.0.0</a> release monitors Codex only.

1. Install and sign in to at least one supported client.
2. Download the current release, or build the utility from source with the instructions below.
3. Double-click the EXE to run it. Each start creates its own tray icon. If an icon is in the hidden icons area, open that area and move the icon to the taskbar if you prefer.

No additional files are needed next to the EXE. Starting the utility again creates another icon; it does not stop or replace the existing one.

Right-click an icon to open the menu. The menu lists the providers. A provider that is installed and signed in is bright and can be selected; a provider that is not detected is gray and cannot be selected. The icon monitors the provider you choose for it, and the choice does not affect other icons. Before you choose, the icon binds to the first detected provider in this order: Codex → Claude → OpenCode. If none is detected, the card shows <b>No provider</b> and asks you to choose one in the menu. The choice is kept in memory only; after a restart the icon binds by default again. <b>Exit</b> closes that icon.

Hover over the icon to open the card. It stays open while you move the pointer from the icon to the card, and hides 500 ms after you move away. Press <kbd>Esc</kbd> to hide the card.

### Launching Codex and the utility together

On Windows, the <code>--with-codex</code> option is available for the Codex desktop app. It opens Codex and starts the tray utility. Using the shortcut again opens Codex and starts another utility icon.

1. Put the EXE in a permanent folder, for example <code>%LOCALAPPDATA%\Programs\AIUsageTray\AIUsageTray.exe</code>.
2. Create a shortcut to the EXE. In the shortcut's **Target** field, add <code>--with-codex</code> at the end:

   ~~~text
   "%LOCALAPPDATA%\Programs\AIUsageTray\AIUsageTray.exe" --with-codex
   ~~~

3. Use this shortcut to start Codex.

Starting the EXE without an option opens only the tray utility. The combined mode uses the registered app ID <code>OpenAI.Codex_2p2nqsd0c76g0!App</code>, so the launch path does not depend on the installed Codex version number. Microsoft documents this activation method through <code>shell:AppsFolder</code> in <a href="https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-transition-users-from-your-web-unpackaged-app-to-store-packaged-app#how-to-launch-the-store-application-from-your-web-unpackaged-app">its documentation</a>.

## What the icon shows

For Codex and Claude the icon shows the **remaining** limit, and each row gets its own color:

| Remaining | Color |
| --- | --- |
| More than 30% | Green |
| More than 10%, up to and including 30% | Yellow |
| 10% or less | Red |
| No reliable data | Gray dash |

For OpenCode the icon shows tokens today on top and estimated cost below, in neutral colors (not the limit scale). The icon has a transparent background and opaque digits. An unknown value is shown as a dash, never as zero.

## Card

All rows are centered. Percentage rows are bold. The window width fits the text. The right edge and taskbar each have a 6-pixel gap. The card has no title bar or system buttons. Dates use the Windows time zone.

The title is <code>&lt;Provider&gt; · PLAN</code>, with the plan in uppercase and bold. Without a plan, only the provider name is shown (for example, <code>Claude</code> or <code>OpenCode</code>).

The data below is an example. The rows shown depend on the provider response.

**Russian UI:**

~~~text
Codex · PLUS

5 часов - 75%
До сброса: 3 ч 20 мин
Сброс: 01.01.2030 18:00

Неделя - 20%
До сброса: 2 дня 8 ч 20 мин
Сброс: 03.01.2030 23:00

Сбросов лимитов: 1
До: 31.01.2030 23:59

Подписка до: 31.01.2030 23:59
~~~

**English UI:**

~~~text
Codex · PLUS

5 hours - 75%
Time to reset: 3 h 20 min
Reset: 01.01.2030 18:00

Week - 20%
Time to reset: 2 days 8 h 20 min
Reset: 03.01.2030 23:00

Limit resets: 1
Until: 31.01.2030 23:59

Subscription until: 31.01.2030 23:59
~~~

Claude shows the same limit rows with its own plan name.

**OpenCode (English UI):**

~~~text
OpenCode

Tokens today - 1,234,567
Cost today (estimated) - $12.34

OpenCode is not running; data from the local database
~~~

The service may return a reset count without a detailed list. Expiry rows are shown when the corresponding entries are available. A saved subscription expiry date may be out of date; it does not confirm the next payment date.

### Language selection

At startup, the UI language is selected using the Windows display language **or regional format**:

- **Russian** is used if either setting belongs to Armenia, Azerbaijan, Belarus, Estonia, Georgia, Kazakhstan, Kyrgyzstan, Latvia, Lithuania, Moldova, Russia, Tajikistan, Turkmenistan, Ukraine, or Uzbekistan.
- **English** is used in all other cases.

For example, an English Windows display language with a Russian regional format selects Russian for the utility. Restart it after changing Windows language settings.

## Getting data

Every 60 seconds the utility reads the selected provider. It creates no files of its own and does not change Windows startup or settings.

- **Codex** — starts a hidden child process, <code>codex app-server --listen stdio://</code>, and reads the limits with <code>account/rateLimits/read</code> (plan name with <code>account/read</code> when needed). Each request has a 15-second timeout and the process exits afterwards; requests do not overlap. It sends no model requests and uses no manual limit resets. Subscription metadata is read locally from Codex's saved authentication data.
- **Claude** — reads the limits through the OAuth usage endpoint that Claude Code uses, with the saved Claude Code token first and the Claude Desktop token as a fallback. The endpoint is unofficial and may change. Without a usable saved token the live limits cannot be shown.
- **OpenCode** — reads the local database through the official <code>opencode db</code> CLI (no network) and shows tokens and estimated cost for today. The cost is an estimate and can differ from the service's billing; it is not a limit.

The utility reads credentials in memory only and never copies or stores tokens. If a request fails, the last successful result stays visible. A warning appears after a failure or when the data is at least 120 seconds old. The countdown updates only while the card is open.

Statuses: when a client is not running but its credentials are present, the card shows a neutral line such as <code>Codex is not running; data from the saved sign-in</code> (OpenCode: <code>data from the local database</code>). If the provider disappears completely after a successful read, the card keeps the last snapshot and adds <code>no data is arriving; the provider is inactive</code>; without a snapshot it shows <code>not available</code>. Unknown data is shown as <code>not available</code>, never as 0% or 100%.

## Build

The source code is in <code>src/</code>. See <a href="docs/BUILDING.md">docs/BUILDING.md</a> for build and release instructions.

The built EXE appears in <code>dist/</code>, which is not included in Git.

Run this command from the project root:

~~~powershell
.\Check-Local.ps1
~~~

The script builds the application in <code>dist/</code>.

## Issues and contributions

Use the repository's **Issues** section to report a problem or suggest an improvement. Include the Windows version, display scaling, steps to reproduce, and expected and actual results. You can propose changes in a pull request with a short description and the relevant check results.

The main source file is <a href="src/AIUsageTray.cs">src/AIUsageTray.cs</a>.

## Third-party code

Some techniques are adapted from <a href="https://github.com/Tooblippe/codex-usage">Tooblippe/codex-usage</a> and <a href="https://github.com/VictorZakharov/codex-usage">VictorZakharov/codex-usage</a>. Their MIT notices are preserved in <a href="THIRD-PARTY-NOTICES.txt">THIRD-PARTY-NOTICES.txt</a> and embedded in the EXE. The source and versions of the adapted parts are listed in that file.
