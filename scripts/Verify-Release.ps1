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

$repoDocument = Get-Content -LiteralPath $repoPath -Raw | ConvertFrom-Json
if ($repoDocument -is [System.Array]) {
    if ($repoDocument.Length -ne 1) { throw 'repo.json must contain exactly one plugin.' }
    $entry = $repoDocument[0]
}
else {
    # PowerShell 7 enumerates a one-element JSON array while Windows PowerShell
    # 5.1 preserves it. Accept both representations, but never more than one
    # repository entry.
    $entry = $repoDocument
}
if ($null -eq $entry -or $entry.PSObject.Properties['InternalName'] -eq $null) {
    throw 'repo.json must contain exactly one plugin object.'
}
$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json

if ($entry.InternalName -ne 'PulseQueue.Plugin') { throw 'Unexpected repo InternalName.' }
if ($sourceManifest.InternalName -ne $entry.InternalName) { throw 'Source and repository InternalName differ.' }
if ([int]$entry.DalamudApiLevel -ne 15 -or [int]$sourceManifest.DalamudApiLevel -ne 15) {
    throw 'Dalamud API level must be 15.'
}
if (-not [bool]$entry.IsTestingExclusive -or -not [bool]$sourceManifest.IsTestingExclusive) {
    throw 'The initial release must remain testing-exclusive.'
}

$effectiveRepoVersion = [string]$entry.AssemblyVersion
$effectiveRepoApiLevel = [int]$entry.DalamudApiLevel
if ([bool]$entry.IsTestingExclusive) {
    if ($null -eq $entry.PSObject.Properties['TestingAssemblyVersion'] -or
        [string]::IsNullOrWhiteSpace([string]$entry.TestingAssemblyVersion)) {
        throw 'Testing-exclusive releases must declare TestingAssemblyVersion.'
    }
    if ($null -eq $entry.PSObject.Properties['TestingDalamudApiLevel']) {
        throw 'Testing-exclusive releases must declare TestingDalamudApiLevel.'
    }
    if ($null -eq $entry.PSObject.Properties['DownloadLinkTesting'] -or
        [string]::IsNullOrWhiteSpace([string]$entry.DownloadLinkTesting)) {
        throw 'Testing-exclusive releases must declare DownloadLinkTesting.'
    }

    $effectiveRepoVersion = [string]$entry.TestingAssemblyVersion
    $effectiveRepoApiLevel = [int]$entry.TestingDalamudApiLevel
}

if ($effectiveRepoApiLevel -ne 15) {
    throw 'The effective repository API level must be 15.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
$temporaryDll = [System.IO.Path]::GetTempFileName()
try {
    $required = @(
        'PulseQueue.Core.dll',
        'PulseQueue.Core.pdb',
        'PulseQueue.Plugin.deps.json',
        'PulseQueue.Plugin.dll',
        'PulseQueue.Plugin.json'
    )
    $names = @($archive.Entries | ForEach-Object FullName)
    foreach ($name in $required) {
        if ($names -notcontains $name) { throw "Release ZIP is missing $name" }
    }
    $unexpected = @($names | Where-Object { $required -notcontains $_ })
    if ($unexpected.Count -ne 0 -or $names.Count -ne $required.Count) {
        throw "Release ZIP contains unexpected entries: $($unexpected -join ', ')"
    }

    $manifestEntry = $archive.GetEntry('PulseQueue.Plugin.json')
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try { $packedManifest = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }

    if ($packedManifest.InternalName -ne $entry.InternalName) { throw 'Packed manifest InternalName differs.' }
    # DalamudPackager emits install-manifest fields only and omits repository
    # channel metadata such as IsTestingExclusive. Source and repo metadata
    # above remain authoritative. A preserved field may not contradict them.
    if (($null -ne $packedManifest.PSObject.Properties['IsTestingExclusive']) -and
        (-not [bool]$packedManifest.IsTestingExclusive)) {
        throw 'Packed manifest contradicts the testing-exclusive source metadata.'
    }
    if ([string]$packedManifest.Description -ne [string]$sourceManifest.Description -or
        [string]$packedManifest.Description -ne [string]$entry.Description) {
        throw 'Packed, source, and repository descriptions differ.'
    }
    if ([string]$packedManifest.AssemblyVersion -ne $effectiveRepoVersion) {
        throw "Packed manifest version differs from effective repo version $effectiveRepoVersion."
    }
    if ([int]$packedManifest.DalamudApiLevel -ne $effectiveRepoApiLevel) {
        throw "Packed manifest API level differs from effective repo API level $effectiveRepoApiLevel."
    }

    $dllEntry = $archive.GetEntry('PulseQueue.Plugin.dll')
    $input = $dllEntry.Open()
    $output = [System.IO.File]::Create($temporaryDll)
    try { $input.CopyTo($output) }
    finally { $output.Dispose(); $input.Dispose() }

    $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($temporaryDll).Version.ToString()
    if ($assemblyVersion -ne $effectiveRepoVersion) {
        throw "DLL version $assemblyVersion differs from effective repo version $effectiveRepoVersion."
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
