using Xunit;
using YubiLatch.Service;

namespace YubiLatch.Service.Tests;

public sealed class DiagnosticTests
{
    [Fact]
    public void DiagnosticErrorOmitsExceptionMessageAndInnerPayload()
    {
        var error = new InvalidOperationException("password-marker", new Exception("key-marker"));
        var entry = ServiceDiagnostics.Format(ServiceStage.ManagementFailed, error);
        Assert.Contains("InvalidOperationException", entry);
        Assert.Contains("HRESULT=0x", entry);
        Assert.DoesNotContain("password-marker", entry);
        Assert.DoesNotContain("key-marker", entry);
        Assert.DoesNotContain("StackTrace", entry);
    }
}
