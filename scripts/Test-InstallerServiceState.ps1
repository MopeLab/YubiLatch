#Requires -Version 7.0
# Exercise installer state transitions with mocked SCM operations; do not modify services.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'installer/Common.ps1')

function Get-ServiceRegistrationState { return $script:state }
function Invoke-Sc([string[]]$ScArguments, [int[]]$SuccessCodes) {
    if ($ScArguments.Count -ne 2 -or $ScArguments[0] -ne 'delete' -or $ScArguments[1] -ne 'YubiLatch') { throw 'Unexpected SCM operation.' }
    if (1072 -notin $SuccessCodes -or 1060 -notin $SuccessCodes) { throw 'Idempotent delete error codes missing.' }
    $script:deleted = $true
}

$script:state = 'Missing'
$script:deleted = $false
Assert-ServiceInstallable
if (-not (Remove-YubiLatchService) -or $script:deleted) { throw 'Missing service must be a no-op.' }

$script:state = 'DeletePending'
if (Wait-ServiceRegistrationRemoved -TimeoutSeconds 0) { throw 'Pending deletion reported as absent.' }
$script:state = 'Missing'
if (-not (Wait-ServiceRegistrationRemoved -TimeoutSeconds 0)) { throw 'Absent registration not detected.' }

function Wait-ServiceRegistrationRemoved { return $script:released }
$script:state = 'DeletePending'
$script:released = $true
Assert-ServiceInstallable
$script:released = $false
$blocked = $false
try { Assert-ServiceInstallable } catch { $blocked = $_.Exception.Message.Contains('等待删除') }
if (-not $blocked) { throw 'Pending deletion must give actionable install guidance.' }
if (Remove-YubiLatchService) { throw 'Pending deletion must remain pending until all handles close.' }
if (-not $script:deleted) { throw 'Pending deletion did not accept repeated delete.' }

$script:state = 'Present'
$blocked = $false
try { Assert-ServiceInstallable } catch { $blocked = $_.Exception.Message.Contains('已安装') }
if (-not $blocked) { throw 'An existing installation must still block overwrite.' }
Write-Output 'Installer service-state checks passed; no real services changed.'
