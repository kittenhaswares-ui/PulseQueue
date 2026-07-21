param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$project = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/PulseQueue.Plugin.csproj'
$output = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/bin/Release'
$packaged = Join-Path $output 'PulseQueue.Plugin/latest.zip'
$dist = Join-Path $RepositoryRoot 'dist'

if (-not $NoBuild) {
    & dotnet restore $project --use-lock-file
    if ($LASTEXITCODE -ne 0) { throw 'Plugin restore failed.' }
    & dotnet build $project -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
}

if (-not (Test-Path -LiteralPath $packaged -PathType Leaf)) {
    throw "Dalamud package was not produced: $packaged"
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null
Copy-Item -LiteralPath $packaged -Destination (Join-Path $dist 'latest.zip') -Force
$zipHash = (Get-FileHash -LiteralPath (Join-Path $dist 'latest.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
$zipHash | Set-Content -LiteralPath (Join-Path $dist 'latest.zip.sha256') -NoNewline
& (Join-Path $PSScriptRoot 'Get-SourceFingerprint.ps1') -RepositoryRoot $RepositoryRoot |
    Set-Content -LiteralPath (Join-Path $dist 'source.sha256') -NoNewline

& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -RepositoryRoot $RepositoryRoot
& (Join-Path $PSScriptRoot 'Verify-SafetyContract.ps1') -RepositoryRoot $RepositoryRoot
