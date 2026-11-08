#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PackageRoot).Path
$manifest = Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.Files) {
    if (-not $names.Add($entry.Path)) { throw 'Duplicate manifest path.' }
    $path = [IO.Path]::GetFullPath((Join-Path $root $entry.Path))
    if (-not $path.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escaped package.' }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "Hash mismatch: $($entry.Path)" }
}
foreach ($required in @('service\YubiLatch.Service.exe','service\coreclr.dll','service\Yubico.NativeShims.dll',
    'app\YubiLatch.App.exe','app\coreclr.dll','app\Microsoft.ui.xaml.dll','provider\YubiLatch.CredentialProvider.dll',
    'installer\Install.ps1','installer\Enable-Provider.ps1','installer\Disable-Provider.ps1','installer\Uninstall.ps1',
    'docs\recovery.md','dependencies.json','licenses\Yubico.YubiKey-1.18.1\LICENSE.txt')) {
    if (-not $names.Contains($required)) { throw "Required payload missing: $required" }
}
# Read the PE header only. Never load a service, provider or hardware library for validation.
$native = [IO.File]::ReadAllBytes((Join-Path $root 'provider\YubiLatch.CredentialProvider.dll'))
$pe = [BitConverter]::ToInt32($native, 0x3c)
if ([BitConverter]::ToUInt32($native, $pe) -ne 0x4550 -or [BitConverter]::ToUInt16($native, $pe + 4) -ne 0x8664) { throw 'Provider is not an x64 PE DLL.' }
Write-Output "Verified $($names.Count) package hashes, self-contained runtime payloads, licenses and x64 PE header. No login components executed."
