using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using YubiLatch.Core;

namespace YubiLatch.Service;

internal static class PasswordValidationWorker
{
    internal sealed class Request
    {
        public string Sid { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
    }
    internal sealed class Reply
    {
        public bool Success { get; set; }
        public bool Available { get; set; } = true;
    }

    public static async Task VerifyAsync(Caller caller, string user, string password, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "YubiLatch.Service.exe"));
        start.ArgumentList.Add("--verify-password");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Request { Sid = caller.Sid, UserName = user, Password = password }, Wire.Json);
        byte[]? response = null;
        try
        {
            response = await ExchangeAsync(start, bytes, TimeSpan.FromSeconds(25), ct);
            var reply = JsonSerializer.Deserialize<Reply>(response, Wire.Json) ?? throw new InvalidDataException();
            if (!reply.Available) throw new CredentialValidationUnavailableException();
            if (!reply.Success) throw new UnauthorizedAccessException("Account verification failed.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or JsonException)
        { throw new CredentialValidationUnavailableException(); }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (response != null) CryptographicOperations.ZeroMemory(response);
        }
    }

    internal static async Task<byte[]> ExchangeAsync(ProcessStartInfo start, byte[] request, TimeSpan timeout, CancellationToken ct)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Credential worker unavailable.");
        var discardErrors = child.StandardError.BaseStream.CopyToAsync(Stream.Null);
        byte[]? reply = null;
        try
        {
            await Wire.WriteFrameAsync(child.StandardInput.BaseStream, request, deadline.Token);
            child.StandardInput.Close();
            reply = await Wire.ReadFrameAsync(child.StandardOutput.BaseStream, deadline.Token);
            await child.WaitForExitAsync(deadline.Token);
            if (child.ExitCode != 0) throw new InvalidOperationException("Credential worker exited unexpectedly.");
            var result = reply;
            reply = null;
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Windows account validation timed out."); }
        finally
        {
            if (reply != null) CryptographicOperations.ZeroMemory(reply);
            // The worker can exit between HasExited and Kill.
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (child.HasExited) { }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await discardErrors.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    // Called only by a LocalSystem child, with secrets delivered through inherited anonymous pipes.
    public static async Task<int> RunAsync()
    {
        byte[]? bytes = null;
        var result = new Reply();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            bytes = await Wire.ReadFrameAsync(Console.OpenStandardInput(), timeout.Token);
            var request = JsonSerializer.Deserialize<Request>(bytes, Wire.Json) ?? throw new InvalidDataException();
            WindowsIdentityChecks.VerifyPassword(new Caller(request.Sid, 0, 0, false), request.UserName, request.Password);
            result.Success = true;
        }
        catch (UnauthorizedAccessException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { result.Available = false; }
        finally { if (bytes != null) CryptographicOperations.ZeroMemory(bytes); }
        var response = JsonSerializer.SerializeToUtf8Bytes(result, Wire.Json);
        await Wire.WriteFrameAsync(Console.OpenStandardOutput(), response, CancellationToken.None);
        return 0;
    }
}

internal sealed class CredentialValidationUnavailableException : Exception;
