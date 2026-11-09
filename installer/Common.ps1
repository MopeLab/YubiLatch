Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ProviderId = '{D53C2D8E-51CC-4F5D-88E6-A0CDF5B39C54}'
$script:ProviderKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$script:ProviderId"
$script:ComKey = "HKLM:\SOFTWARE\Classes\CLSID\$script:ProviderId"
$script:InstallRoot = Join-Path $env:ProgramFiles 'MopeLab\YubiLatch'
$script:VaultRoot = Join-Path $env:ProgramData 'MopeLab\YubiLatch'

function Assert-Administrator {
    if (-not [Environment]::Is64BitProcess) { throw '请使用 64 位 PowerShell。' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw '请以管理员身份运行 PowerShell。'
        }
    } finally { $identity.Dispose() }
}

function Assert-NoReparse([string]$Path) {
    $node = [IO.Path]::GetFullPath($Path)
    while ($node) {
        if (Test-Path -LiteralPath $node) {
            if ((Get-Item -LiteralPath $node -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "不允许链接或重解析点: $node"
            }
        }
        $node = [IO.Path]::GetDirectoryName($node)
    }
}

function Assert-ChildPath([string]$Path, [string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if ($resolved -ne $base -and -not $resolved.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw '路径超出目标目录。'
    }
    Assert-NoReparse $resolved
    return $resolved
}

function Set-ProtectedDirectory([string]$Path, [bool]$AllowRead) {
    Assert-NoReparse $Path
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $acl.SetOwner($admins)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach ($sid in @($admins, $system)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, 'None', 'Allow'))
    }
    if ($AllowRead) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'ReadAndExecute', $inherit, 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Assert-TrustedParent([string]$Path) {
    Assert-NoReparse $Path
    $acl = Get-Acl -LiteralPath $Path
    $trusted = @('S-1-5-18','S-1-5-32-544')
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw "目录所有者不可信: $Path" }
    foreach ($rule in $acl.Access) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $trusted) {
            if ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership') {
                throw "目录允许非管理员写入: $Path"
            }
        }
    }
}

function Assert-NoReparseTree([string]$Root) {
    Assert-NoReparse $Root
    $queue = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $queue.Push([IO.DirectoryInfo]::new($Root))
    while ($queue.Count) {
        foreach ($entry in $queue.Pop().EnumerateFileSystemInfos()) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "目录树包含重解析点，拒绝递归删除: $($entry.FullName)" }
            if ($entry -is [IO.DirectoryInfo]) { $queue.Push($entry) }
        }
    }
}

function Remove-ProviderRegistration {
    foreach ($key in @($script:ProviderKey, $script:ComKey)) {
        if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction Stop }
        if (Test-Path -LiteralPath $key) { throw '无法移除 Provider 注册；停止清理文件，请先通过恢复步骤禁用。' }
    }
}

function Invoke-Sc([string[]]$ScArguments, [int[]]$SuccessCodes = @(0)) {
    $output = & "$env:SystemRoot\System32\sc.exe" @ScArguments
    $code = $LASTEXITCODE
    if ($code -notin $SuccessCodes) { $output | Out-Host; throw "Service Control 命令失败 ($code)。" }
    if ($code -eq 0) { $output }
}

function Get-ServiceRegistrationState {
    if (-not ('YubiLatch.Installer.ServiceProbe' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace YubiLatch.Installer {
    public static class ServiceProbe {
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint size, out uint needed);
        [DllImport("advapi32.dll")]
        static extern bool CloseServiceHandle(IntPtr handle);
        public static string Read() {
            var manager = OpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) throw new Win32Exception();
            try {
                var service = OpenService(manager, "YubiLatch", 5);
                if (service == IntPtr.Zero) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 1060) return "Missing";
                    if (error == 1072) return "DeletePending";
                    throw new Win32Exception(error);
                }
                try {
                    uint needed;
                    QueryServiceConfig(service, IntPtr.Zero, 0, out needed);
                    int error = Marshal.GetLastWin32Error();
                    if (error == 2 || error == 1072) return "DeletePending";
                    if (error != 122) throw new Win32Exception(error);
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\YubiLatch")) {
                        if (key != null && Convert.ToInt32(key.GetValue("DeleteFlag", 0)) == 1) return "DeletePending";
                    }
                    return "Present";
                } finally { CloseServiceHandle(service); }
            } finally { CloseServiceHandle(manager); }
        }
    }
}
'@
    }
    return [YubiLatch.Installer.ServiceProbe]::Read()
}

function Wait-ServiceRegistrationRemoved([int]$TimeoutSeconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ((Get-ServiceRegistrationState) -eq 'Missing') { return $true }
        if ([DateTime]::UtcNow -ge $deadline) { return $false }
        Start-Sleep -Milliseconds 250
    } while ($true)
}

function Assert-ServiceInstallable {
    $state = Get-ServiceRegistrationState
    if ($state -eq 'Present') { throw '已安装 YubiLatch，请先运行 Uninstall.ps1 卸载旧版本。' }
    if ($state -eq 'DeletePending' -and -not (Wait-ServiceRegistrationRemoved)) {
        throw 'YubiLatch 服务正在等待删除。请关闭服务管理器、服务属性窗口及相关管理终端后重试；若仍未释放，请重启 Windows。'
    }
}

function Remove-YubiLatchService {
    $state = Get-ServiceRegistrationState
    if ($state -eq 'Missing') { return $true }
    if ($state -eq 'Present') {
        $controller = [ServiceProcess.ServiceController]::new('YubiLatch')
        try {
            if ($controller.Status -ne 'Stopped') {
                $controller.Stop()
                $controller.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
            }
        } catch {
            if ((Get-ServiceRegistrationState) -eq 'Present') { throw }
        } finally { $controller.Dispose() }
    }
    # Repeating delete is successful when already missing or marked for deletion.
    Invoke-Sc -ScArguments @('delete','YubiLatch') -SuccessCodes @(0,1060,1072) | Out-Host
    return (Wait-ServiceRegistrationRemoved)
}

function Get-OwnAccountStatus {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'MopeLab.YubiLatch.Management.v1', [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::None, [Security.Principal.TokenImpersonationLevel]::Identification)
    try {
        $pipe.Connect(2500)
        $pipe.ReadMode = [IO.Pipes.PipeTransmissionMode]::Byte
        # No password or mutation is sent by this installer-only status request.
        $payload = [Text.Encoding]::UTF8.GetBytes('{"Operation":"Status"}')
        $length = [BitConverter]::GetBytes([int]$payload.Length)
        $pipe.Write($length, 0, 4)
        $pipe.Write($payload, 0, $payload.Length)
        $header = [byte[]]::new(4)
        Read-Exact $pipe $header
        $count = [BitConverter]::ToInt32($header, 0)
        if ($count -le 0 -or $count -gt 32768) { throw '无效的服务响应。' }
        $bytes = [byte[]]::new($count)
        Read-Exact $pipe $bytes
        $result = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        if (-not $result.Success -or -not $result.Account) { throw '无法读取绑定状态。' }
        return $result.Account
    } finally { $pipe.Dispose() }
}

function Read-Exact([IO.Stream]$Stream, [byte[]]$Buffer) {
    # Async wait supplies a timeout even with Windows PowerShell 5.1 named pipes.
    $offset = 0
    while ($offset -lt $Buffer.Length) {
        $pending = $Stream.BeginRead($Buffer, $offset, $Buffer.Length - $offset, $null, $null)
        if (-not $pending.AsyncWaitHandle.WaitOne(5000)) { $Stream.Dispose(); throw '读取服务状态超时。' }
        $read = $Stream.EndRead($pending)
        if ($read -le 0) { throw '服务连接已关闭。' }
        $offset += $read
    }
}
