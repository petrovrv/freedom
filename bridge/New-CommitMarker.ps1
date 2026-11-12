# SPDX-License-Identifier: AGPL-3.0-or-later
param(
    [Parameter(Mandatory=$true)]
    [string]$Payload,

    [Parameter(Mandatory=$false)]
    [string]$Marker
)

$resolved = (Resolve-Path $Payload).Path

if ([string]::IsNullOrWhiteSpace($Marker)) {
    $Marker = [System.IO.Path]::ChangeExtension($resolved, ".ready")
} else {
    $Marker = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Marker)
}

$item = Get-Item $resolved
$hash = (Get-FileHash -Algorithm SHA256 $resolved).Hash.ToLowerInvariant()

$object = [ordered]@{
    schemaVersion = 2
    payload       = [System.IO.Path]::GetFileName($resolved)
    sizeBytes     = [int64]$item.Length
    sha256        = $hash
}

$json = $object | ConvertTo-Json -Compress
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($Marker, $json, $utf8NoBom)

Write-Host "Marker written:" $Marker
Write-Host "SHA-256:" $hash
Write-Host "Size:" $item.Length
