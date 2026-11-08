using System.Security.Cryptography;
using Yubico.YubiKey;
using Yubico.YubiKey.Otp;
using YubiLatch.Core;

namespace YubiLatch.Service;

internal sealed class Hardware
{
    private static IYubiKeyDevice Find(int serial) => YubiKeyDevice.FindAll()
        .SingleOrDefault(d => d.SerialNumber == serial) ?? throw new IOException("Key unavailable.");

    public IReadOnlyList<DeviceStatus> List()
    {
        var result = new List<DeviceStatus>();
        foreach (var device in YubiKeyDevice.FindAll())
        {
            if (device.SerialNumber is not > 0) continue;
            try
            {
                using var otp = new OtpSession(device);
                result.Add(new DeviceStatus(device.SerialNumber.Value, "YubiKey " + device.FormFactor, otp.IsLongPressConfigured, otp.LongPressRequiresTouch));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* Do not log SDK data. */ }
        }
        return result;
    }

    public void RequireEmpty(int serial)
    {
        using var otp = new OtpSession(Find(serial));
        if (otp.IsLongPressConfigured) throw new InvalidOperationException("Slot 2 is occupied.");
    }

    public void Provision(KeyBinding binding)
    {
        using var otp = new OtpSession(Find(binding.Serial));
        if (otp.IsLongPressConfigured) throw new InvalidOperationException("Slot 2 is occupied.");
        // This is the ONLY device-writing operation in the application. Never overwrite/reset.
        otp.ConfigureChallengeResponse(Slot.LongPress).UseHmacSha1().UseKey(binding.Secret).UseButton().Execute();
    }

    public bool Prove(KeyBinding binding)
    {
        using var otp = new OtpSession(Find(binding.Serial));
        if (!otp.IsLongPressConfigured || !otp.LongPressRequiresTouch) return false;
        using var proof = new ChallengeProof();
        var challenge = proof.CopyChallenge();
        byte[]? response = null;
        try
        {
            // The SDK performs an actual USB HID HMAC transaction; the key enforces physical touch.
            response = otp.CalculateChallengeResponse(Slot.LongPress).UseChallenge(challenge)
                .UseYubiOtp(false).UseTouchNotifier(() => { }).GetDataBytes().ToArray();
            return proof.Verify(binding.Secret, response);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challenge);
            if (response != null) CryptographicOperations.ZeroMemory(response);
        }
    }
}
