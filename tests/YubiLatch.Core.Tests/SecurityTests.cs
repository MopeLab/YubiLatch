using System.Buffers.Binary;
using System.Security.Cryptography;
using YubiLatch.Core;
using Xunit;

namespace YubiLatch.Core.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void ValidHardwareProtocolProofIsSingleUse()
    {
        using var proof = new ChallengeProof();
        var secret = RandomNumberGenerator.GetBytes(20);
        var response = HMACSHA1.HashData(secret, proof.CopyChallenge());
        Assert.True(proof.Verify(secret, response));
        Assert.False(proof.Verify(secret, response));
    }

    [Fact]
    public void FreshChallengeRejectsRecordedResponse()
    {
        var secret = RandomNumberGenerator.GetBytes(20);
        using var previous = new ChallengeProof();
        using var next = new ChallengeProof();
        Assert.NotEqual(previous.CopyChallenge(), next.CopyChallenge());
        Assert.False(next.Verify(secret, HMACSHA1.HashData(secret, previous.CopyChallenge())));
    }

    [Theory]
    [InlineData(0)] [InlineData(19)] [InlineData(21)] [InlineData(64)]
    public void MalformedResponseConsumesAttempt(int length)
    {
        using var proof = new ChallengeProof();
        var secret = RandomNumberGenerator.GetBytes(20);
        Assert.False(proof.Verify(secret, new byte[length]));
        Assert.False(proof.Verify(secret, HMACSHA1.HashData(secret, proof.CopyChallenge())));
    }

    [Fact]
    public void OtherKeyAndTamperedResponseAreRejected()
    {
        using var proof = new ChallengeProof();
        var secret = RandomNumberGenerator.GetBytes(20);
        var response = HMACSHA1.HashData(RandomNumberGenerator.GetBytes(20), proof.CopyChallenge());
        Assert.False(proof.Verify(secret, response));
    }

    [Fact]
    public void DisposedProofCannotAuthenticate()
    {
        var proof = new ChallengeProof();
        var secret = RandomNumberGenerator.GetBytes(20);
        var response = HMACSHA1.HashData(secret, proof.CopyChallenge());
        proof.Dispose();
        Assert.False(proof.Verify(secret, response));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(32769)] [InlineData(int.MaxValue)]
    public async Task UntrustedLengthRejectedBeforeAllocation(int length)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        await Assert.ThrowsAsync<InvalidDataException>(() => Wire.ReadFrameAsync(new MemoryStream(bytes), default));
    }

    [Fact]
    public async Task TruncatedFrameIsRejected()
    {
        byte[] bytes = [10, 0, 0, 0, 1, 2];
        await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.ReadFrameAsync(new MemoryStream(bytes), default));
    }

    [Fact]
    public async Task MaximumFrameRoundTrips()
    {
        using var stream = new MemoryStream();
        var bytes = RandomNumberGenerator.GetBytes(Wire.MaxFrame);
        await Wire.WriteFrameAsync(stream, bytes, default);
        stream.Position = 0;
        Assert.Equal(bytes, await Wire.ReadFrameAsync(stream, default));
    }

    [Fact]
    public void AccountRejectsSidMismatchDuplicateKeysAndInvalidSecret()
    {
        using var account = ValidAccount();
        Assert.Throws<InvalidDataException>(() => account.Validate("S-1-5-21-999"));
        account.Keys.Add(new KeyBinding { Serial = 1, Secret = new byte[19], Name = "备用" });
        Assert.Throws<InvalidDataException>(() => account.Validate(account.Sid));
        account.Keys[0].Secret = new byte[20];
        account.Keys.Add(new KeyBinding { Serial = 1, Secret = new byte[20], Name = "重复" });
        Assert.Throws<InvalidDataException>(() => account.Validate(account.Sid));
    }

    [Fact]
    public void AccountDisposalWipesAllSecrets()
    {
        var account = ValidAccount();
        account.Keys.Add(new KeyBinding { Serial = 1, Secret = RandomNumberGenerator.GetBytes(20) });
        account.Dispose();
        Assert.All(account.PasswordUtf16, b => Assert.Equal(0, b));
        Assert.All(account.Keys[0].Secret, b => Assert.Equal(0, b));
    }

    [Fact]
    public void InvalidProviderAndOddPasswordAreRejected()
    {
        using var account = ValidAccount();
        account.QualifiedUserName = "AzureAD\\a";
        Assert.Throws<InvalidDataException>(() => account.Validate(account.Sid));
        account.QualifiedUserName = "MicrosoftAccount\\a@b.example";
        account.PasswordUtf16 = [1, 2, 3];
        Assert.Throws<InvalidDataException>(() => account.Validate(account.Sid));
    }

    private static AccountRecord ValidAccount() => new()
    {
        Sid = "S-1-5-21-123", QualifiedUserName = "MicrosoftAccount\\a@b.example", PasswordUtf16 = [65, 0]
    };
}
