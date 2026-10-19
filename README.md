# AI Usage Tray — Codex only

[English](README.md) | [Русский](README.ru.md)

Version 1.0.0 supports Codex only.

A compact Windows utility that shows your remaining five-hour and weekly Codex limits in the system tray. Two percentages on the icon let you monitor both limits while you work. Hover over the icon to see more details.

Run the utility from a single file, <code>AIUsageTray.exe</code>. It uses your installed Codex app and its saved sign-in.

## Features

- Two percentage rows on one icon: the five-hour limit is on top and the weekly limit is below.
- Each row has its own color based on the remaining limit. The full “5 hours” and “Week” rows in the card use the same status colors.
- The plan name after “Codex ·” is shown in uppercase and bold.
- The card opens after 500 ms of continuous hovering. Moving the pointer away before it appears cancels the opening.
- The number of available manual limit resets and the expiry dates of returned reset entries.
- The subscription expiry date, when it is available in Codex's saved metadata.
- The Windows system font and light or dark theme, with a width that fits the content.
- Russian and English UI with automatic language selection.
- Data refreshes every 60 seconds. The card marks data as out of date when a request fails or the data gets old.

## Requirements

- 64-bit Windows. The current build has been checked on Windows 11.
- .NET Framework 4.8.1.
- Codex with the native <code>codex.exe</code> that supports <code>app-server</code>, signed in to an account.
- Only Codex is supported in 1.0.0. Claude and OpenCode are not supported in this release.
- Codex must be able to reach its service to get current usage limits.

The utility locates <code>codex.exe</code> through <code>PATH</code> or the installed Codex app package. The Codex window can be closed.

## Run

1. Install Codex and sign in.
2. Download <a href="https://github.com/btc072/AI-Usage-Tray/releases/latest/download/AIUsageTray.exe">AIUsageTray.exe</a>, or build it using the instructions below.
3. Double-click the EXE to run it. If the icon is in the hidden icons area, open that area and move the icon to the taskbar if you prefer.

No additional files are needed next to the EXE. If you start the utility again, it keeps only one running instance.

Hover over the icon to open the card. It stays open while you move the pointer from the icon to the card, and hides 500 ms after you move away. Press <kbd>Esc</kbd> to hide the card. Right-click the icon to open the <b>Exit</b> menu.

### Launching Codex and the utility together

On Windows, the <code>--with-codex</code> option is available for the Codex desktop app. It opens Codex and starts the tray utility. Using the shortcut again opens Codex and keeps a single utility instance.

1. Put the EXE downloaded from GitHub Releases in a permanent folder, for example <code>%LOCALAPPDATA%\Programs\AIUsageTray\AIUsageTray.exe</code>.
2. Create a shortcut to the EXE. In the shortcut's **Target** field, add <code>--with-codex</code> at the end:

   ~~~text
   "%LOCALAPPDATA%\Programs\AIUsageTray\AIUsageTray.exe" --with-codex
   ~~~

3. Use this shortcut to start Codex.

Starting the EXE without an option opens only the tray utility. The combined mode uses the registered app ID <code>OpenAI.Codex_2p2nqsd0c76g0!App</code>, so the launch path does not depend on the installed Codex version number. Microsoft documents this activation method through <code>shell:AppsFolder</code> in <a href="https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-transition-users-from-your-web-unpackaged-app-to-store-packaged-app#how-to-launch-the-store-application-from-your-web-unpackaged-app">its documentation</a>.

## What the icon shows

The icon shows the **remaining** limit. Each row gets its own color.

| Remaining | Color |
| --- | --- |
| More than 30% | Green |
| More than 10%, up to and including 30% | Yellow |
| 10% or less | Red |
| No reliable data | Gray dash |

The icon has a transparent background and opaque digits. An unknown value is not shown as zero.

## Card

All rows are centered. Percentage rows are bold. The window width fits the text. The right edge and taskbar each have a 6-pixel gap. The card has no title bar or system buttons. Dates use the Windows time zone.

The data below is an example. The rows shown depend on the Codex response.

**Russian UI:**

~~~text
Codex · Plus

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
Codex · Plus

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

The service may return a reset count without a detailed list. Expiry rows are shown when the corresponding entries are available. A saved subscription expiry date may be out of date; it does not confirm the next payment date.

### Language selection

At startup, the UI language is selected using the Windows display language **or regional format**:

- **Russian** is used if either setting belongs to Armenia, Azerbaijan, Belarus, Estonia, Georgia, Kazakhstan, Kyrgyzstan, Latvia, Lithuania, Moldova, Russia, Tajikistan, Turkmenistan, Ukraine, or Uzbekistan.
- **English** is used in all other cases.

For example, an English Windows display language with a Russian regional format selects Russian for the utility. Restart it after changing Windows language settings.

## Getting data

Every 60 seconds, the utility starts a hidden child process, <code>codex app-server --listen stdio://</code>, reads the usage limits, and exits the process. Each request has a 15-second timeout. Requests do not overlap.

The utility calls <code>account/rateLimits/read</code>. If needed, it gets the plan name with <code>account/read</code>. It does not send model requests or use manual limit resets. The installed Codex app handles sign-in. Subscription metadata is read locally from Codex's saved authentication data. The utility does not save tokens in its own files.

If a request fails, the last successful result stays visible. A warning appears after a failure or when the data is at least 120 seconds old. The countdown updates only while the card is open. The utility does not change Windows startup or settings.

## Build

The source code is in <code>src/</code>. See <a href="docs/BUILDING.md">docs/BUILDING.md</a> for build and release instructions.

The built EXE appears in <code>build/</code>, which is not included in Git. The GitHub Release includes the file for users.

Run this command from the project root:

~~~powershell
.\Check-Local.ps1
~~~

The script builds the application in <code>build/</code>.

## Issues and contributions

Use the repository's **Issues** section to report a problem or suggest an improvement. Include the Windows version, display scaling, steps to reproduce, and expected and actual results. You can propose changes in a pull request with a short description and the relevant check results.

The main source file is <a href="src/AIUsageTray.cs">src/AIUsageTray.cs</a>.

## Third-party code

Some techniques are adapted from <a href="https://github.com/Tooblippe/codex-usage">Tooblippe/codex-usage</a> and <a href="https://github.com/VictorZakharov/codex-usage">VictorZakharov/codex-usage</a>. Their MIT notices are preserved in <a href="THIRD-PARTY-NOTICES.txt">THIRD-PARTY-NOTICES.txt</a> and embedded in the EXE. The source and versions of the adapted parts are listed in that file.
