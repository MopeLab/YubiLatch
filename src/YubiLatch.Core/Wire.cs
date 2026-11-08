using System.Buffers.Binary;
using System.Text.Json;

namespace YubiLatch.Core;

public static class Wire
{
    public const string ManagementPipe = "MopeLab.YubiLatch.Management.v1";
    public const string LogonPipe = "MopeLab.YubiLatch.Logon.v1";
    public const uint Magic = 0x314C4259; // YBL1, little endian
    public const int MaxFrame = 32768;
    public const int MaxSidBytes = 184;
    public const int MaxPasswordBytes = 1024;
    public const uint Query = 1;
    public const uint Acquire = 2;
    public const uint Ready = 0;
    public const uint Unavailable = 1;
    public const uint Failed = 2;

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false, MaxDepth = 12 };

    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxFrame) throw new InvalidDataException("Frame length out of range.");
        var bytes = new byte[length];
        try { await stream.ReadExactlyAsync(bytes, ct); return bytes; }
        catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length is <= 0 or > MaxFrame) throw new InvalidDataException("Frame length out of range.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
}
