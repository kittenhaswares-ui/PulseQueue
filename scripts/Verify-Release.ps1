param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$zipPath = Join-Path $RepositoryRoot 'dist/latest.zip'
$zipHashPath = Join-Path $RepositoryRoot 'dist/latest.zip.sha256'
$sourceFingerprintPath = Join-Path $RepositoryRoot 'dist/source.sha256'
$repoPath = Join-Path $RepositoryRoot 'repo.json'
$sourceManifestPath = Join-Path $RepositoryRoot 'src/PulseQueue.Plugin/PulseQueue.Plugin.json'

foreach ($path in @($zipPath, $zipHashPath, $sourceFingerprintPath, $repoPath, $sourceManifestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release file is missing: $path"
    }
}

$expectedFingerprint = (Get-Content -LiteralPath $sourceFingerprintPath -Raw).Trim().ToLowerInvariant()
$actualFingerprint = (& (Join-Path $PSScriptRoot 'Get-SourceFingerprint.ps1') -RepositoryRoot $RepositoryRoot).Trim().ToLowerInvariant()
if ($expectedFingerprint -ne $actualFingerprint) {
    throw 'Published ZIP is stale: source fingerprint changed.'
}

$repo = @(Get-Content -LiteralPath $repoPath -Raw | ConvertFrom-Json)
if ($repo.Count -ne 1) { throw 'repo.json must contain exactly one plugin.' }
$entry = $repo[0]
$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json

if ($entry.InternalName -ne 'PulseQueue.Plugin') { throw 'Unexpected repo InternalName.' }
if ($sourceManifest.InternalName -ne $entry.InternalName) { throw 'Source and repository InternalName differ.' }
if ([int]$entry.DalamudApiLevel -ne 15 -or [int]$sourceManifest.DalamudApiLevel -ne 15) {
    throw 'Dalamud API level must be 15.'
}
if (-not [bool]$entry.IsTestingExclusive -or -not [bool]$sourceManifest.IsTestingExclusive) {
    throw 'The initial release must remain testing-exclusive.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
$temporaryDll = [System.IO.Path]::GetTempFileName()
try {
    $required = @(
        'PulseQueue.Core.dll',
        'PulseQueue.Plugin.deps.json',
        'PulseQueue.Plugin.dll',
        'PulseQueue.Plugin.json'
    )
    $names = @($archive.Entries | ForEach-Object FullName)
    foreach ($name in $required) {
        if ($names -notcontains $name) { throw "Release ZIP is missing $name" }
    }

    $manifestEntry = $archive.GetEntry('PulseQueue.Plugin.json')
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try { $packedManifest = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }

    if ($packedManifest.InternalName -ne $entry.InternalName) { throw 'Packed manifest InternalName differs.' }
    if ($packedManifest.AssemblyVersion -ne $entry.AssemblyVersion) { throw 'Packed manifest version differs.' }

    $dllEntry = $archive.GetEntry('PulseQueue.Plugin.dll')
    $input = $dllEntry.Open()
    $output = [System.IO.File]::Create($temporaryDll)
    try { $input.CopyTo($output) }
    finally { $output.Dispose(); $input.Dispose() }

    $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($temporaryDll).Version.ToString()
    if ($assemblyVersion -ne $entry.AssemblyVersion) {
        throw "DLL version $assemblyVersion differs from repo version $($entry.AssemblyVersion)."
    }
}
finally {
    $archive.Dispose()
    Remove-Item -LiteralPath $temporaryDll -Force -ErrorAction SilentlyContinue
}

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedZipHash = (Get-Content -LiteralPath $zipHashPath -Raw).Trim().ToLowerInvariant()
if ($hash -ne $expectedZipHash) { throw 'Release ZIP hash does not match dist/latest.zip.sha256.' }
Write-Host "PulseQueue release verified: $($entry.AssemblyVersion) / SHA256 $hash"
