using System.Security.Cryptography;

namespace YubiLatch.Core;

public enum BindingState { Pending, Active }

public sealed class KeyBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Serial { get; set; }
    public string Name { get; set; } = "YubiKey";
    public byte[] Secret { get; set; } = [];
    public BindingState State { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

// Only the SYSTEM service owns decrypted instances. Never return these through IPC or log them.
public sealed class AccountRecord : IDisposable
{
    public int Schema { get; set; } = 1;
    public string Sid { get; set; } = "";
    public string QualifiedUserName { get; set; } = "";
    public byte[] PasswordUtf16 { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public List<KeyBinding> Keys { get; set; } = [];

    public void Validate(string expectedSid)
    {
        if (Schema != 1 || Sid != expectedSid || !QualifiedUserName.StartsWith("MicrosoftAccount\\", StringComparison.OrdinalIgnoreCase)
            || QualifiedUserName.Length > 320 || QualifiedUserName.Contains('\0')
            || PasswordUtf16.Length is < 2 or > 1024 || PasswordUtf16.Length % 2 != 0
            || Keys.Count > 16 || Keys.Select(k => k.Id).Distinct().Count() != Keys.Count
            || Keys.Select(k => k.Serial).Distinct().Count() != Keys.Count
            || Keys.Any(k => k.Secret.Length != 20 || k.Serial <= 0 || k.Name.Length is < 1 or > 64
                || !Enum.IsDefined(k.State)))
            throw new InvalidDataException("Invalid account record.");
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(PasswordUtf16);
        foreach (var key in Keys) CryptographicOperations.ZeroMemory(key.Secret);
    }
}

public sealed record KeyStatus(Guid Id, int Serial, string Name, BindingState State, DateTimeOffset CreatedUtc);
public sealed record DeviceStatus(int Serial, string Name, bool Slot2Occupied, bool Slot2RequiresTouch);
public sealed record AccountStatus(bool Enrolled, bool Enabled, string UserName, IReadOnlyList<KeyStatus> Keys);

public sealed record ManagementRequest
{
    public string Operation { get; init; } = "Status";
    public string UserName { get; init; } = "";
    public string Password { get; init; } = "";
    public int Serial { get; init; }
    public string KeyName { get; init; } = "";
    public Guid KeyId { get; init; }
    public bool Enabled { get; init; }
    public bool RecoveryConfirmed { get; init; }
    public bool EmptySlotConsent { get; init; }
}

public sealed record ManagementReply(bool Success, string Code, string Message,
    AccountStatus? Account = null, IReadOnlyList<DeviceStatus>? Devices = null);
