param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256Hex([byte[]]$Bytes) {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($Bytes)
        return [BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

$repositoryFullPath = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
    [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)) + [System.IO.Path]::DirectorySeparatorChar

$sourceRoots = @(
    (Join-Path $RepositoryRoot 'src/PulseQueue.Core'),
    (Join-Path $RepositoryRoot 'src/PulseQueue.Plugin')
)

$lines = foreach ($root in $sourceRoots) {
    Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object {
            $fileFullPath = [System.IO.Path]::GetFullPath($_.FullName)
            if (-not $fileFullPath.StartsWith($repositoryFullPath, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to fingerprint a source outside the repository: $fileFullPath"
            }
            $relative = $fileFullPath.Substring($repositoryFullPath.Length).Replace('\', '/')
            $content = [System.IO.File]::ReadAllText($_.FullName).Replace("`r`n", "`n").Replace("`r", "`n")
            $contentBytes = [System.Text.Encoding]::UTF8.GetBytes($content)
            $fileHash = Get-Sha256Hex $contentBytes
            "$relative`:$fileHash"
        }
}

$canonical = ($lines | Sort-Object) -join "`n"
$bytes = [System.Text.Encoding]::UTF8.GetBytes($canonical)
Get-Sha256Hex $bytes
