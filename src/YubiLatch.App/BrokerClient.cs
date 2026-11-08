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
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    public static async Task<ManagementReply> Send(ManagementRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(72));
        using var pipe = new NamedPipeClientStream(".", Wire.ManagementPipe, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(1500, timeout.Token);
        // Authenticate the endpoint BEFORE sending any password. A pipe name is not an identity.
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new Win32Exception();
        var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == 0) throw new Win32Exception();
        try
        {
            if (!OpenProcessToken(process, 8 /* TOKEN_QUERY */, out var token)) throw new Win32Exception();
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                if (!identity.IsSystem) throw new UnauthorizedAccessException();
        }
        finally { CloseHandle(process); }
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
