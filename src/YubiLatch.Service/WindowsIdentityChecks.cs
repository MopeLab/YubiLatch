using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace YubiLatch.Service;

internal sealed record Caller(string Sid, int Session, int ProcessId, bool IsSystem);

internal static partial class WindowsIdentityChecks
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUser(string user, string domain, string password, int type, int provider, out SafeAccessTokenHandle token);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformation(nint server, int session, int info, out nint buffer, out int bytes);
    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);

    public static Caller Inspect(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) throw new Win32Exception();
        using var process = Process.GetProcessById(checked((int)pid));
        string? sid = null;
        var system = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(true) ?? throw new UnauthorizedAccessException();
            sid = identity.User?.Value;
            system = identity.IsSystem;
        });
        return new Caller(sid ?? throw new UnauthorizedAccessException(), process.SessionId, process.Id, system);
    }

    public static void RequireLogonUi(Caller caller, int requestedSession)
    {
        if (!caller.IsSystem || caller.Session != requestedSession || requestedSession <= 0) throw new UnauthorizedAccessException();
        using var process = Process.GetProcessById(caller.ProcessId);
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "LogonUI.exe");
        if (!string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
        if (!WTSQuerySessionInformation(0, requestedSession, 16 /* WTSClientProtocolType */, out var buffer, out var bytes))
            throw new UnauthorizedAccessException();
        try { if (bytes < 2 || Marshal.ReadInt16(buffer) != 0) throw new UnauthorizedAccessException("Console only."); }
        finally { WTSFreeMemory(buffer); }
    }

    public static string VerifyPassword(Caller caller, string inputUser, string password)
    {
        var user = inputUser.StartsWith("MicrosoftAccount\\", StringComparison.OrdinalIgnoreCase) ? inputUser[17..] : inputUser;
        if (user.Length is < 3 or > 255 || !user.Contains('@') || user.Contains('\\') || user.Contains('\0')
            || password.Length is < 1 or > 512 || password.Contains('\0'))
            throw new UnauthorizedAccessException();
        if (!LogonUser(user, "MicrosoftAccount", password, 2 /* INTERACTIVE */, 0, out var token))
            throw new UnauthorizedAccessException("Account verification failed.");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (identity.User?.Value != caller.Sid) throw new UnauthorizedAccessException("Account SID mismatch.");
        }
        return "MicrosoftAccount\\" + user;
    }
}
