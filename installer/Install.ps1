#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$PackageRoot = (Split-Path $PSScriptRoot -Parent),
    [switch]$AllowUnsignedDevelopmentBuild
)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator
if ([Environment]::OSVersion.Version.Build -lt 22621) { throw '要求 Windows 11 22H2 或更高版本。' }
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) { throw '目前仅支持 x64。' }
$source = (Resolve-Path -LiteralPath $PackageRoot).Path
Assert-NoReparse $source
$manifest = Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.Schema -ne 1 -or $manifest.Files.Count -lt 3) { throw '无效的发布清单。' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.Files) {
    if ([IO.Path]::IsPathRooted($entry.Path) -or $entry.Path.Contains(':')) { throw '无效的清单路径。' }
    $file = Assert-ChildPath (Join-Path $source $entry.Path) $source
    if (-not $seen.Add($entry.Path) -or -not (Test-Path -LiteralPath $file -PathType Leaf)) { throw '清单文件缺失或重复。' }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "文件校验失败: $($entry.Path)" }
}
foreach ($required in @('service\YubiLatch.Service.exe','app\YubiLatch.App.exe','provider\YubiLatch.CredentialProvider.dll','installer\Uninstall.ps1')) {
    if (-not $seen.Contains($required)) { throw "缺少发布组件: $required" }
}
if (-not $AllowUnsignedDevelopmentBuild) {
    foreach ($entry in $manifest.Files) {
        if ([IO.Path]::GetExtension($entry.Path) -notin @('.exe','.dll')) { continue }
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $source $entry.Path)
        if ($signature.Status -ne 'Valid') { throw "所有发布 EXE/DLL 必须有有效 Authenticode 签名（包括托管程序集和原生依赖）: $($entry.Path)。源码开发包需显式使用 -AllowUnsignedDevelopmentBuild。" }
    }
}
if (Get-Service -Name YubiLatch -ErrorAction SilentlyContinue) { throw '已存在 YubiLatch 服务。请先卸载旧版本（默认保留绑定），不要覆盖运行中的登录 DLL。' }
if (Test-Path -LiteralPath $script:InstallRoot) { throw '安装目录已存在，请先处理旧安装；本安装器不会覆盖现有文件。' }
Assert-NoReparse $script:InstallRoot
Assert-NoReparse $script:VaultRoot
if (-not $PSCmdlet.ShouldProcess($script:InstallRoot, '安装服务和管理器（不会注册登录 Provider）')) { return }
$createdService = $false
$createdDirectory = $false
try {
    $binaryParent = Split-Path $script:InstallRoot -Parent
    if (-not (Test-Path -LiteralPath $binaryParent)) { Set-ProtectedDirectory $binaryParent $true }
    Assert-TrustedParent $binaryParent
    Set-ProtectedDirectory $script:InstallRoot $true
    $createdDirectory = $true
    # Keep the common parent protected against directory renames by ordinary users.
    $dataParent = Split-Path $script:VaultRoot -Parent
    if (-not (Test-Path -LiteralPath $dataParent)) { Set-ProtectedDirectory $dataParent $false }
    Assert-TrustedParent $dataParent
    Set-ProtectedDirectory $script:VaultRoot $false
    foreach ($entry in $manifest.Files) {
        $target = Assert-ChildPath (Join-Path $script:InstallRoot $entry.Path) $script:InstallRoot
        [IO.Directory]::CreateDirectory((Split-Path $target -Parent)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $source $entry.Path) -Destination $target
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.Sha256) { throw '复制后的文件校验失败。' }
    }
    Copy-Item -LiteralPath (Join-Path $source 'manifest.json') -Destination (Join-Path $script:InstallRoot 'manifest.json')
    $exe = Join-Path $script:InstallRoot 'service\YubiLatch.Service.exe'
    New-Service -Name YubiLatch -BinaryPathName ('"' + $exe + '"') -DisplayName 'YubiLatch Authentication Broker' -StartupType Automatic -Description 'Local YubiKey challenge-response and protected Windows credential broker.' | Out-Null
    $createdService = $true
    # Only SYSTEM and administrators may change service configuration; users may query status.
    Invoke-Sc @('sdset','YubiLatch','D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;BU)')
    Invoke-Sc @('failure','YubiLatch','reset=','86400','actions=','restart/5000/restart/15000/none/0')
    Start-Service -Name YubiLatch
    (Get-Service -Name YubiLatch).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
    $uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\YubiLatch'
    New-Item -Path $uninstallKey -Force | Out-Null
    New-ItemProperty -LiteralPath $uninstallKey -Name DisplayName -Value 'YubiLatch' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $uninstallKey -Name DisplayVersion -Value $manifest.Version -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $uninstallKey -Name Publisher -Value 'MopeLab' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $uninstallKey -Name UninstallString -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $script:InstallRoot 'installer\Uninstall.ps1') + '"') -PropertyType String -Force | Out-Null
    $menu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\YubiLatch.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($menu)
    $shortcut.TargetPath = Join-Path $script:InstallRoot 'app\YubiLatch.App.exe'
    $shortcut.WorkingDirectory = Join-Path $script:InstallRoot 'app'
    $shortcut.Save()
    Write-Output '安装完成。尚未注册登录 Provider。请打开 YubiLatch 绑定钥匙，并阅读 README 的启用步骤。'
} catch {
    $originalError = $_
    if ($createdService) {
        Stop-Service -Name YubiLatch -Force -ErrorAction SilentlyContinue
        & "$env:SystemRoot\System32\sc.exe" delete YubiLatch | Out-Null
    }
    if ($createdDirectory) {
        $checked = Assert-ChildPath $script:InstallRoot (Join-Path $env:ProgramFiles 'MopeLab')
        Assert-NoReparseTree $checked
        Remove-Item -LiteralPath $checked -Recurse -Force
    }
    Remove-Item -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\YubiLatch' -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\YubiLatch.lnk') -Force -ErrorAction SilentlyContinue
    throw $originalError
}
