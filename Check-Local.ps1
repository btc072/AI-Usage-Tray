$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sourceDir = Join-Path $projectRoot 'src'
$notices = Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt'
$distDir = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))

$sources = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' -File |
    Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) {
    throw "No C# sources found in $sourceDir"
}

foreach ($file in @($compiler, $notices) + $sources) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required file not found: $file"
    }
}
if (-not $distDir.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output directory must be inside the project'
}
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$common = @(
    '/nologo', '/langversion:5', '/platform:x64', '/optimize+',
    '/warn:4', '/warnaserror+', '/utf8output',
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    '/r:System.Web.Extensions.dll',
    "/resource:$notices,AIUsageTray.ThirdPartyNotices"
)

& $compiler @common /target:winexe /main:AIUsageTray.Program `
    "/out:$distDir\AIUsageTray.exe" $sources
if ($LASTEXITCODE -ne 0) { throw "Product build failed with exit code $LASTEXITCODE" }

Write-Host 'PASS: local build'


