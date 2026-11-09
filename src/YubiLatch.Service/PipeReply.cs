using YubiLatch.Core;

namespace YubiLatch.Service;

internal static class PipeReply
{
    public static async Task WriteManagementAsync(Stream pipe, ReadOnlyMemory<byte> response, CancellationToken ct)
    {
        await Wire.WriteFrameAsync(pipe, response, ct);
        // DisconnectNamedPipe discards unread output, even after FlushAsync. The client closes
        // after consuming the complete reply; wait for that close before disconnecting.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try { await pipe.ReadExactlyAsync(new byte[1], deadline.Token); }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}
