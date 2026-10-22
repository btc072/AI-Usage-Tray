param(
    [string]$Root
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
$sqlite = (Get-Command sqlite3.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $sqlite) {
    $candidates = @(
        'C:\.Tools\AndroidSDK\platform-tools\sqlite3.exe',
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\sqlite3.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $sqlite = $candidate; break }
    }
}
if (-not $sqlite) { throw 'sqlite3.exe was not found; set it in PATH.' }

if (-not $Root) {
    $Root = Join-Path $tempRoot ('ai-usage-tray-t7-' + [Guid]::NewGuid().ToString('N'))
}
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if (-not $Root.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "T7 Root must be inside ${tempRoot}: $Root"
}
if (Test-Path -LiteralPath $Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container) -or
        (Get-ChildItem -LiteralPath $Root -Force | Select-Object -First 1)) {
        throw "T7 Root must be empty or new: $Root"
    }
}
New-Item -ItemType Directory -Path $Root -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Root 'opencode') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Root 'bin') -Force | Out-Null

$db = Join-Path $Root 'opencode\opencode.db'
$utcNow = [DateTimeOffset]::UtcNow
$dayStart = $utcNow.AddHours(-$utcNow.Hour).AddMinutes(-$utcNow.Minute).AddSeconds(-$utcNow.Second).AddMilliseconds(-$utcNow.Millisecond)
$startMs = $dayStart.ToUnixTimeMilliseconds()
$prevMs = $startMs - 1
$rowMs = $utcNow.ToUnixTimeMilliseconds()
$nextMs = $startMs + 86400000
$sql = @"
PRAGMA journal_mode=DELETE;
CREATE TABLE message (id TEXT PRIMARY KEY, time_created INTEGER NOT NULL, data TEXT NOT NULL);
CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, time_created INTEGER NOT NULL, data TEXT NOT NULL);
INSERT INTO message VALUES ('prev', $prevMs, '{"providerID":"opencode","modelID":"deepseek-v4.1-flash-free","role":"assistant"}');
INSERT INTO part VALUES ('prev-part', 'prev', $prevMs, '{"type":"step-finish","tokens":{"input":1,"output":0,"reasoning":0,"cache":{"read":0,"write":0}},"cost":0.01}');
INSERT INTO message VALUES ('boundary-error', $startMs, '{"role":"assistant","error":{"name":"FreeUsageLimitError","data":{"message":"free usage exceeded"}}}');
INSERT INTO message VALUES ('today', $rowMs, '{"providerID":"opencode","modelID":"deepseek-v4.1-flash-free","role":"assistant"}');
INSERT INTO part VALUES ('today-part', 'today', $rowMs, '{"type":"step-finish","tokens":{"input":10,"output":2,"reasoning":0,"cache":{"read":0,"write":0}},"cost":0.1}');
INSERT INTO message VALUES ('today-error', $($rowMs + 1), '{"providerID":"opencode","modelID":"deepseek-v4.1-flash-free","role":"assistant","error":{"name":"FreeUsageLimitError","data":{"message":"free usage exceeded"}}}');
INSERT INTO message VALUES ('boundary-next', $nextMs, '{"providerID":"opencode","modelID":"deepseek-v4.1-flash-free","role":"assistant"}');
INSERT INTO part VALUES ('boundary-next-part', 'boundary-next', $nextMs, '{"type":"step-finish","tokens":{"input":99,"output":0,"reasoning":0,"cache":{"read":0,"write":0}},"cost":99}');
"@
$sql | & $sqlite $db | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Failed to create the temporary SQLite database.' }

$stub = Join-Path $Root 'bin\opencode.exe'
& $compiler /nologo /langversion:5 /platform:x64 /target:winexe /warn:4 `
    "/out:$stub" (Join-Path $PSScriptRoot 'OpenCodeStub.cs')
if ($LASTEXITCODE -ne 0) { throw 'Failed to build the test OpenCode stub.' }

$source = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -File |
    Sort-Object Name | ForEach-Object { $_.FullName })
$testExe = Join-Path $Root 'T7Tests.exe'
& $compiler /nologo /langversion:5 /platform:x64 /target:exe /warn:4 `
    '/r:System.Windows.Forms.dll' '/r:System.Drawing.dll' '/r:System.Web.Extensions.dll' `
    '/main:AIUsageTray.T7Tests' "/out:$testExe" $source (Join-Path $PSScriptRoot 'T7Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Failed to build T7Tests.' }

$names = @('XDG_DATA_HOME', 'PATH', 'T7_ROOT', 'T7_DB', 'T7_SQLITE', 'T7_NOW', 'T7_PID_FILE', 'T7_DELAY_MS', 'T7_CHILD', 'T7_EXIT_EARLY')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$report = Join-Path $Root 'report.txt'
$code = 1
try {
    $env:XDG_DATA_HOME = $Root
    $env:PATH = (Join-Path $Root 'bin') + ';' + $env:PATH
    $env:T7_ROOT = $Root
    $env:T7_DB = $db
    $env:T7_SQLITE = $sqlite
    $env:T7_NOW = $utcNow.ToUnixTimeMilliseconds().ToString()
    $env:T7_PID_FILE = Join-Path $Root 'stub-pids.txt'
    $stdout = Join-Path $Root 'test-stdout.txt'
    $stderr = Join-Path $Root 'test-stderr.txt'
    $process = Start-Process -FilePath $testExe -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit(120000)) {
        & "$env:WINDIR\System32\taskkill.exe" /PID $process.Id /T /F | Out-Null
        $code = 1
        Write-Host 'FAIL: T7 tests exceeded the 120 second watchdog'
    }
    else {
        $code = $process.ExitCode
    }
    $process.Dispose()
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout | Tee-Object -FilePath $report -Append }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr | Tee-Object -FilePath $report -Append }
}
finally {
    foreach ($name in $names) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
    }
}
Write-Host "T7 root: $Root"
Write-Host "T7 report: $report"
exit $code
