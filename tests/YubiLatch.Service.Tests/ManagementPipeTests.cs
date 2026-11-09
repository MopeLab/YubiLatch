using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Xunit;
using YubiLatch.Core;
using YubiLatch.Service;

namespace YubiLatch.Service.Tests;

public sealed class ManagementPipeTests
{
    [Fact]
    public async Task NewConnectionsReadRequestBeforeInspectingClientIdentity()
    {
        var name = "YubiLatch.Tests." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var identity = WindowsIdentity.GetCurrent();
        for (var i = 0; i < 3; i++)
        {
            var accept = server.WaitForConnectionAsync(timeout.Token);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await client.ConnectAsync(timeout.Token);
            await accept;
            // Execute the production read/identity sequence before the client has written data.
            var request = WindowsIdentityChecks.ReadManagementAsync(server, timeout.Token);
            Assert.False(request.IsCompleted);
            var payload = Encoding.UTF8.GetBytes("{\"Operation\":\"Status\"}");
            await Wire.WriteFrameAsync(client, payload, timeout.Token);
            var (caller, received) = await request;
            Assert.Equal(payload, received);
            Assert.Equal(identity.User!.Value, caller.Sid);
            Assert.Equal(Environment.ProcessId, caller.ProcessId);
            server.Disconnect();
        }
    }
}
