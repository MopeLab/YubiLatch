#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$exe = Join-Path (Resolve-Path -LiteralPath $AppDirectory).Path 'YubiLatch.App.exe'
# Launch only the management GUI, from an unrelated working directory. No enrollment/device actions.
$child = Start-Process -FilePath $exe -WorkingDirectory $env:TEMP -WindowStyle Hidden -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    $windowSince = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $child.Refresh()
        if ($child.HasExited) { throw "Published GUI exited before startup completed (exit code $($child.ExitCode))." }
        if ($child.MainWindowHandle -ne [IntPtr]::Zero) {
            if ($null -eq $windowSince) { $windowSince = [DateTime]::UtcNow }
            if (([DateTime]::UtcNow - $windowSince).TotalSeconds -ge 5) {
                Write-Output 'Published management GUI created a window and remained running.'
                return
            }
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'Published GUI did not create a window within 25 seconds.'
} finally {
    $child.Refresh()
    if (-not $child.HasExited) {
        $child.CloseMainWindow() | Out-Null
        if (-not $child.WaitForExit(5000)) { $child.Kill(); $child.WaitForExit() }
    }
    $child.Dispose()
}
