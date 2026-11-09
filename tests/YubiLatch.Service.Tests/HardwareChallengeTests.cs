using System.Security.Cryptography;
using Xunit;
using YubiLatch.Core;
using YubiLatch.Service;

namespace YubiLatch.Service.Tests;

public sealed class HardwareChallengeTests
{
    [Fact]
    public void HmacSlotWithoutKeyboardTriggerStillPerformsChallengeVerification()
    {
        var secret = RandomNumberGenerator.GetBytes(20);
        var session = new Session(secret);
        var hardware = new Hardware(_ => session);
        Assert.False(session.KeyboardTouchTriggered);
        Assert.True(hardware.Prove(new KeyBinding { Serial = 1, Secret = secret }));
        Assert.True(session.Called);
    }

    [Fact]
    public void ConfiguredSlotWithWrongSecretIsRejected()
    {
        var session = new Session(RandomNumberGenerator.GetBytes(20));
        var hardware = new Hardware(_ => session);
        Assert.False(hardware.Prove(new KeyBinding { Serial = 1, Secret = RandomNumberGenerator.GetBytes(20) }));
        Assert.True(session.Called);
    }

    [Fact]
    public void EmptySlotDoesNotRequestAChallenge()
    {
        var session = new Session(new byte[20]) { IsConfigured = false };
        var hardware = new Hardware(_ => session);
        Assert.False(hardware.Prove(new KeyBinding { Serial = 1, Secret = new byte[20] }));
        Assert.False(session.Called);
    }

    private sealed class Session(byte[] secret) : IOtpChallengeSession
    {
        public bool IsConfigured { get; init; } = true;
        public bool KeyboardTouchTriggered => false;
        public bool Called { get; private set; }
        public byte[] Calculate(byte[] challenge) { Called = true; return HMACSHA1.HashData(secret, challenge); }
        public void Dispose() { }
    }
}
