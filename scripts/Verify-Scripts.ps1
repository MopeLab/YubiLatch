#Requires -Version 7.0
# Parse only. Never dot-source or execute an installer as part of verification.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$failed = $false
foreach ($folder in @('installer','scripts')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root $folder) -Filter '*.ps1') {
        $parseErrors = $null
        $tokens = $null
        [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
        if ($parseErrors.Count) { $failed = $true; $parseErrors | ForEach-Object { Write-Error -ErrorAction Continue ($file.Name + ': ' + $_.Message) } }
    }
}
if ($failed) { throw 'PowerShell parsing failed.' }
Write-Output 'Installer/build scripts parsed successfully; no installation actions executed.'
