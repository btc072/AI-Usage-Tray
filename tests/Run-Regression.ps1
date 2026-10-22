param(
    [Parameter(Mandatory=$true)][string]$Binary,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [switch]$Gui
)
$ErrorActionPreference = 'Stop'
$Binary = [IO.Path]::GetFullPath($Binary)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $Binary -PathType Leaf)) { throw 'Application binary is required' }
if (Test-Path -LiteralPath $OutputRoot) { throw 'Use a new isolated output directory' }
New-Item -ItemType Directory -Path (Join-Path $OutputRoot 'bin') -Force | Out-Null
foreach ($name in @('codex','opencode')) {
    & $compiler /nologo /langversion:5 /platform:x64 /warn:4 /warnaserror+ /target:exe "/out:$OutputRoot\bin\$name.exe" (Join-Path $PSScriptRoot 'CliStub.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Stub compilation failed' }
}
& $compiler /nologo /langversion:5 /platform:x64 /warn:4 /warnaserror+ /target:exe "/out:$OutputRoot\RegressionTests.exe" (Join-Path $PSScriptRoot 'RegressionTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Regression runner compilation failed' }
& (Join-Path $OutputRoot 'RegressionTests.exe') $Binary $OutputRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Gui) {
    foreach ($name in @('GuiTests','LaunchTests')) {
        & $compiler /nologo /langversion:5 /platform:x64 /warn:4 /warnaserror+ /target:exe '/r:System.Windows.Forms.dll' '/r:System.Drawing.dll' "/out:$OutputRoot\$name.exe" (Join-Path $PSScriptRoot "$name.cs")
        if ($LASTEXITCODE -ne 0) { throw 'GUI runner compilation failed' }
        & (Join-Path $OutputRoot "$name.exe") $Binary $OutputRoot
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
exit 0
