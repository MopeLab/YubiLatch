using System.Security.Cryptography;

namespace YubiLatch.Core;

// A proof belongs to one attempt; it cannot be verified twice, even after a failed comparison.
public sealed class ChallengeProof : IDisposable
{
    public const int ChallengeSize = 64;
    public const int ResponseSize = 20;
    private readonly byte[] _challenge = RandomNumberGenerator.GetBytes(ChallengeSize);
    private int _consumed;
    public byte[] CopyChallenge() => (byte[])_challenge.Clone();

    public bool Verify(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> response)
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0 || secret.Length != ResponseSize || response.Length != ResponseSize)
            return false;
        Span<byte> expected = stackalloc byte[ResponseSize];
        try
        {
            HMACSHA1.HashData(secret, _challenge, expected);
            return CryptographicOperations.FixedTimeEquals(expected, response);
        }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _consumed, 1);
        CryptographicOperations.ZeroMemory(_challenge);
    }
}
