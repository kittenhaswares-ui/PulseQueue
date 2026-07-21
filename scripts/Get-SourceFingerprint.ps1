param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sourceRoots = @(
    (Join-Path $RepositoryRoot 'src/PulseQueue.Core'),
    (Join-Path $RepositoryRoot 'src/PulseQueue.Plugin')
)

$lines = foreach ($root in $sourceRoots) {
    Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object {
            $relative = [System.IO.Path]::GetRelativePath($RepositoryRoot, $_.FullName).Replace('\', '/')
            $content = [System.IO.File]::ReadAllText($_.FullName).Replace("`r`n", "`n").Replace("`r", "`n")
            $contentBytes = [System.Text.Encoding]::UTF8.GetBytes($content)
            $fileHash = [Convert]::ToHexString(
                [System.Security.Cryptography.SHA256]::HashData($contentBytes)).ToLowerInvariant()
            "$relative`:$fileHash"
        }
}

$canonical = ($lines | Sort-Object) -join "`n"
$bytes = [System.Text.Encoding]::UTF8.GetBytes($canonical)
$fingerprint = [System.Security.Cryptography.SHA256]::HashData($bytes)
[Convert]::ToHexString($fingerprint).ToLowerInvariant()
