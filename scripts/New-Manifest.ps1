#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PackageRoot).Path
$entries = @(Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object { $_.Name -ne 'manifest.json' } | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = [IO.Path]::GetRelativePath($root, $_.FullName); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[ordered]@{ Schema = 1; Version = '0.1.0-preview'; Files = $entries } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'manifest.json') -Encoding utf8
