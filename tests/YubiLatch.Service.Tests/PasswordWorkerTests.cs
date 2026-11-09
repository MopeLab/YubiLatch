using System.Diagnostics;
using System.Text;
using Xunit;
using YubiLatch.Service;

namespace YubiLatch.Service.Tests;

public sealed class PasswordWorkerTests
{
    [Fact]
    public async Task StalledNativeWorkerIsBoundedAndTerminated()
    {
        // An isolated sleeper stands in for a blocked native API; never calls account APIs.
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[Threading.Thread]::Sleep(30000)");
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => PasswordValidationWorker.ExchangeAsync(start, Encoding.UTF8.GetBytes("test-only"), TimeSpan.FromSeconds(1), default));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task BlockedWorkerInputIsAlsoBounded()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[Threading.Thread]::Sleep(30000)");
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => PasswordValidationWorker.ExchangeAsync(start, new byte[32000], TimeSpan.FromSeconds(1), default));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WorkerExitWithoutReplyDoesNotAuthenticate()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("exit 7");
        await Assert.ThrowsAnyAsync<IOException>(() => PasswordValidationWorker.ExchangeAsync(start, Encoding.UTF8.GetBytes("test-only"), TimeSpan.FromSeconds(10), default));
    }
}
