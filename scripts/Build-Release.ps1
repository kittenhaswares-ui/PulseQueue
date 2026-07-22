param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($NoBuild) {
    throw '-NoBuild publication is forbidden because it can publish stale or unverified binaries.'
}

$project = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/PulseQueue.Plugin.csproj'
$coreProject = Join-Path $RepositoryRoot 'src/PulseQueue.Core/PulseQueue.Core.csproj'
$selfTestProject = Join-Path $RepositoryRoot 'tests/PulseQueue.Core.SelfTest/PulseQueue.Core.SelfTest.csproj'
$output = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/bin/Release'
$packaged = Join-Path $output 'PulseQueue.Plugin/latest.zip'
$dist = Join-Path $RepositoryRoot 'dist'

& dotnet restore $selfTestProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Core self-test restore failed in locked mode.' }
& dotnet build $selfTestProject -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Core self-test build failed.' }
& dotnet run --project $selfTestProject -c Release --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Core one-shot/Turbo invariants failed.' }

& dotnet format $coreProject --verify-no-changes --no-restore
if ($LASTEXITCODE -ne 0) { throw 'PulseQueue.Core format verification failed.' }
& dotnet format $selfTestProject --verify-no-changes --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Core self-test format verification failed.' }

& (Join-Path $PSScriptRoot 'Verify-SafetyContract.ps1') -RepositoryRoot $RepositoryRoot

& dotnet restore $project --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Plugin restore failed in locked mode.' }
& dotnet format $project --verify-no-changes --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Plugin format verification failed.' }

# DalamudPackager's incremental target can leave an older latest.zip in place
# even when the assemblies were rebuilt. Delete only this validated generated
# package before the release build, then require the build to create it anew.
$repositoryFullPath = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
    [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)) + [System.IO.Path]::DirectorySeparatorChar
$packagedFullPath = [System.IO.Path]::GetFullPath($packaged)
if (-not $packagedFullPath.StartsWith($repositoryFullPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove a generated package outside the repository: $packagedFullPath"
}
if (Test-Path -LiteralPath $packagedFullPath -PathType Leaf) {
    Remove-Item -LiteralPath $packagedFullPath -Force
}
$packageBuildStartedAt = [DateTime]::UtcNow
& dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }

if (-not (Test-Path -LiteralPath $packaged -PathType Leaf)) {
    throw "Dalamud package was not produced: $packaged"
}
$packagedInfo = Get-Item -LiteralPath $packaged
if ($packagedInfo.LastWriteTimeUtc -lt $packageBuildStartedAt.AddSeconds(-1)) {
    throw "Dalamud package predates the current release build: $packaged"
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null
Copy-Item -LiteralPath $packaged -Destination (Join-Path $dist 'latest.zip') -Force
$zipHash = (Get-FileHash -LiteralPath (Join-Path $dist 'latest.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
$zipHash | Set-Content -LiteralPath (Join-Path $dist 'latest.zip.sha256') -NoNewline
& (Join-Path $PSScriptRoot 'Get-SourceFingerprint.ps1') -RepositoryRoot $RepositoryRoot |
    Set-Content -LiteralPath (Join-Path $dist 'source.sha256') -NoNewline

& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -RepositoryRoot $RepositoryRoot
