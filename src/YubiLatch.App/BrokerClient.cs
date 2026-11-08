using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using YubiLatch.Core;

namespace YubiLatch.App;

internal static partial class BrokerClient
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);
    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(nint manager, string name, uint access);
    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(nint service, int level, out ServiceStatus status, int size, out int needed);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type, State, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }

    public static async Task<ManagementReply> Send(ManagementRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(72));
        using var pipe = new NamedPipeClientStream(".", Wire.ManagementPipe, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(1500, timeout.Token);
        // Authenticate the endpoint BEFORE sending any password. A pipe name is not an identity.
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new Win32Exception();
        // SCM's protected service-to-PID mapping is readable by standard users. Opening a SYSTEM
        // process token is not reliably permitted and would break non-elevated management.
        var manager = OpenSCManager(null, null, 1 /* SC_MANAGER_CONNECT */);
        if (manager == 0) throw new Win32Exception();
        try
        {
            var service = OpenService(manager, "YubiLatch", 4 /* SERVICE_QUERY_STATUS */);
            if (service == 0) throw new Win32Exception();
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _)) throw new Win32Exception();
                if (status.State != 4 /* RUNNING */ || status.ProcessId != pid) throw new UnauthorizedAccessException();
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Wire.Json);
        byte[]? response = null;
        try
        {
            await Wire.WriteFrameAsync(pipe, bytes, timeout.Token);
            response = await Wire.ReadFrameAsync(pipe, timeout.Token);
            return JsonSerializer.Deserialize<ManagementReply>(response, Wire.Json) ?? throw new InvalidDataException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (response != null) CryptographicOperations.ZeroMemory(response);
        }
    }
}
