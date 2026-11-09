using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using YubiLatch.Core;

namespace YubiLatch.Service;

internal sealed class Broker(Vault vault, Hardware hardware, ILogger<Broker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _passwordAttempts = [];

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Listen(Wire.ManagementPipe, false, stoppingToken), Listen(Wire.LogonPipe, true, stoppingToken));

    private async Task Listen(string name, bool logon, CancellationToken stoppingToken)
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        if (!logon) acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, acl);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(75));
                if (logon) await Logon(pipe, deadline.Token);
                else await Manage(pipe, deadline.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Only error classification; never include request data, exception messages or secrets.
                logger.LogWarning("Pipe {Kind}: {ErrorType}, HRESULT {ErrorCode:X8}",
                    logon ? "Logon" : "Management", ex.GetType().Name, ex.HResult);
            }
            finally { if (pipe.IsConnected) pipe.Disconnect(); }
        }
    }

    private async Task Manage(NamedPipeServerStream pipe, CancellationToken ct)
    {
        ManagementReply reply;
        byte[]? bytes = null;
        try
        {
            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            Caller caller;
            (caller, bytes) = await WindowsIdentityChecks.ReadManagementAsync(pipe, readDeadline.Token);
            var request = JsonSerializer.Deserialize<ManagementRequest>(bytes, Wire.Json) ?? throw new InvalidDataException();
            if (caller.IsSystem || caller.Session <= 0) throw new UnauthorizedAccessException();
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2), ct))
                reply = new(false, "Busy", "另一项操作正在进行，请稍后重试。");
            else
            {
                try { reply = HandleManagement(caller, request); }
                finally { _gate.Release(); }
            }
        }
        catch (UnauthorizedAccessException) { reply = new(false, "AccountVerificationFailed", "账户验证失败。请使用当前 Microsoft 账户密码，而不是 PIN；多次失败后请稍后重试。"); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { reply = new(false, "OperationFailed", "操作未完成。请检查钥匙、槽位和触摸状态；若已写入槽位，请恢复待验证绑定，不要覆盖槽位。"); }
        finally { if (bytes != null) CryptographicOperations.ZeroMemory(bytes); }
        var response = JsonSerializer.SerializeToUtf8Bytes(reply, Wire.Json);
        await PipeReply.WriteManagementAsync(pipe, response, ct);
    }

    private void RequirePassword(Caller caller, string user, string password)
    {
        // Limit failed validations before LogonUser to reduce account lockout abuse.
        if (!_passwordAttempts.TryGetValue(caller.Sid, out var attempts)) _passwordAttempts[caller.Sid] = attempts = new();
        var now = DateTimeOffset.UtcNow;
        while (attempts.TryPeek(out var oldest) && now - oldest > TimeSpan.FromMinutes(5)) attempts.Dequeue();
        if (attempts.Count >= 5) throw new UnauthorizedAccessException();
        try { WindowsIdentityChecks.VerifyPassword(caller, user, password); attempts.Clear(); }
        catch (UnauthorizedAccessException) { attempts.Enqueue(now); throw; }
    }

    private ManagementReply HandleManagement(Caller caller, ManagementRequest request)
    {
        if (request.Operation == "Devices") return new(true, "Ok", "设备状态已更新。", Devices: hardware.List());
        using var existing = vault.Load(caller.Sid);
        if (request.Operation == "Status") return new(true, "Ok", "本地服务已连接。", Snapshot(existing));
        var user = existing?.QualifiedUserName ?? request.UserName;
        RequirePassword(caller, user, request.Password);
        switch (request.Operation)
        {
            case "Enroll":
            {
                if (!request.RecoveryConfirmed || !request.EmptySlotConsent || request.Serial <= 0
                    || string.IsNullOrWhiteSpace(request.KeyName) || request.KeyName.Length > 64)
                    throw new InvalidDataException();
                if (existing?.Keys.Any(k => k.Serial == request.Serial) == true || existing?.Keys.Count >= 16)
                    return new(false, "AlreadyBound", "该钥匙已有绑定或已达 16 把上限。待验证绑定请使用恢复验证。");
                hardware.RequireEmpty(request.Serial);
                using var created = existing == null ? new AccountRecord
                {
                    Sid = caller.Sid,
                    QualifiedUserName = "MicrosoftAccount\\" + (user.StartsWith("MicrosoftAccount\\", StringComparison.OrdinalIgnoreCase) ? user[17..] : user),
                    PasswordUtf16 = Encoding.Unicode.GetBytes(request.Password)
                } : null;
                var account = existing ?? created!;
                byte[] access;
                do { access = RandomNumberGenerator.GetBytes(6); } while (access.All(b => b == 0));
                var binding = new KeyBinding { Serial = request.Serial, Name = request.KeyName.Trim(), Secret = RandomNumberGenerator.GetBytes(20), AccessCode = access };
                account.Keys.Add(binding);
                // Persist first so interruption can be recovered without rewriting the hardware slot.
                vault.Save(account);
                hardware.Provision(binding);
                if (!hardware.Prove(binding)) throw new CryptographicException();
                binding.State = BindingState.Active;
                vault.Save(account);
                return new(true, "Ok", "绑定完成。硬件挑战响应及触摸验证已通过。", Snapshot(account), SlotManagementCode: Convert.ToHexString(binding.AccessCode));
            }
            case "ResumePending":
            {
                var account = existing ?? throw new InvalidDataException();
                var key = account.Keys.Single(k => k.Id == request.KeyId && k.State == BindingState.Pending);
                if (!hardware.Prove(key)) throw new CryptographicException();
                key.State = BindingState.Active;
                vault.Save(account);
                return new(true, "Ok", "待验证绑定已恢复。", Snapshot(account));
            }
            case "RemoveKey":
            {
                var account = existing ?? throw new InvalidDataException();
                var key = account.Keys.Single(k => k.Id == request.KeyId);
                CryptographicOperations.ZeroMemory(key.Secret);
                CryptographicOperations.ZeroMemory(key.AccessCode);
                account.Keys.Remove(key);
                vault.Save(account);
                return new(true, "Ok", "本机绑定已撤销。硬件槽位未被擦除。", Snapshot(account));
            }
            case "GetSlotManagementCode":
            {
                var account = existing ?? throw new InvalidDataException();
                var key = account.Keys.Single(k => k.Id == request.KeyId);
                return new(true, "Ok", "请将槽位管理码安全保管，用于将来通过 Yubico 工具解除槽位保护或重新配置。",
                    SlotManagementCode: Convert.ToHexString(key.AccessCode));
            }
            case "SetEnabled":
            {
                var account = existing ?? throw new InvalidDataException();
                if (request.Enabled && !request.RecoveryConfirmed) throw new InvalidDataException();
                account.Enabled = request.Enabled;
                vault.Save(account);
                return new(true, "Ok", account.Enabled ? "YubiLatch 登录已启用。" : "已暂停，请使用 Windows 原生登录方式。", Snapshot(account));
            }
            case "UpdatePassword":
            {
                var account = existing ?? throw new InvalidDataException();
                CryptographicOperations.ZeroMemory(account.PasswordUtf16);
                account.PasswordUtf16 = Encoding.Unicode.GetBytes(request.Password);
                vault.Save(account);
                return new(true, "Ok", "加密凭据已更新。", Snapshot(account));
            }
            case "ForgetAll":
                vault.Delete(caller.Sid);
                return new(true, "Ok", "本账户的本机凭据与绑定已删除。硬件配置保留。", Snapshot(null));
            default: throw new InvalidDataException();
        }
    }

    private static AccountStatus Snapshot(AccountRecord? account) => account == null
        ? new(false, false, "", [])
        : new(true, account.Enabled, account.QualifiedUserName,
            account.Keys.Select(k => new KeyStatus(k.Id, k.Serial, k.Name, k.State, k.CreatedUtc)).ToArray());

    private async Task Logon(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readDeadline.CancelAfter(TimeSpan.FromSeconds(3));
        var header = new byte[16];
        await pipe.ReadExactlyAsync(header, readDeadline.Token);
        var caller = WindowsIdentityChecks.Inspect(pipe);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Wire.Magic) throw new InvalidDataException();
        var operation = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var session = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var sidLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        if ((operation != Wire.Query && operation != Wire.Acquire) || sidLength is < 10 or > Wire.MaxSidBytes || sidLength % 2 != 0)
            throw new InvalidDataException();
        WindowsIdentityChecks.RequireLogonUi(caller, session);
        var sidBytes = new byte[sidLength];
        await pipe.ReadExactlyAsync(sidBytes, readDeadline.Token);
        var sid = Encoding.Unicode.GetString(sidBytes);
        _ = new SecurityIdentifier(sid);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var disconnect = WatchDisconnect(pipe, connection);
        try
        {
            await ServeLogon(pipe, operation, sid, connection.Token);
            try { await disconnect.WaitAsync(TimeSpan.FromSeconds(3), ct); }
            catch (TimeoutException) { }
        }
        finally { connection.Cancel(); await disconnect; }
    }

    private static async Task WatchDisconnect(Stream pipe, CancellationTokenSource connection)
    {
        try
        {
            // The request is complete; any further input or EOF cancels the attempt.
            await pipe.ReadExactlyAsync(new byte[1], connection.Token);
            connection.Cancel();
        }
        catch (OperationCanceledException) { }
        catch (IOException) { connection.Cancel(); }
    }

    private async Task ServeLogon(Stream pipe, uint operation, string sid, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(1), ct)) { await ReplyLogon(pipe, Wire.Unavailable, ReadOnlyMemory<byte>.Empty, ct); return; }
        try
        {
            using var account = vault.Load(sid);
            if (account == null || !account.Enabled || !account.Keys.Any(k => k.State == BindingState.Active))
            { await ReplyLogon(pipe, Wire.Unavailable, ReadOnlyMemory<byte>.Empty, ct); return; }
            if (operation == Wire.Query) { await ReplyLogon(pipe, Wire.Ready, ReadOnlyMemory<byte>.Empty, ct); return; }
            var until = DateTimeOffset.UtcNow.AddSeconds(55);
            while (DateTimeOffset.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                var attached = hardware.List().Select(d => d.Serial).ToHashSet();
                var candidates = account.Keys.Where(k => k.State == BindingState.Active && attached.Contains(k.Serial)).ToArray();
                // Avoid ambiguous touch prompts. The user should connect exactly one bound key.
                if (candidates.Length > 1) { await ReplyLogon(pipe, Wire.Failed, ReadOnlyMemory<byte>.Empty, ct); return; }
                if (candidates.Length == 1)
                {
                    bool verified;
                    try { verified = hardware.Prove(candidates[0]); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { verified = false; }
                    ct.ThrowIfCancellationRequested();
                    await ReplyLogon(pipe, verified ? Wire.Ready : Wire.Failed,
                        verified ? account.PasswordUtf16 : ReadOnlyMemory<byte>.Empty, ct);
                    return;
                }
                await Task.Delay(500, ct);
            }
            await ReplyLogon(pipe, Wire.Unavailable, ReadOnlyMemory<byte>.Empty, ct);
        }
        finally { _gate.Release(); }
    }

    private static async Task ReplyLogon(Stream pipe, uint status, ReadOnlyMemory<byte> password, CancellationToken ct)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Wire.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), status);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), password.Length);
        await pipe.WriteAsync(header, ct);
        if (!password.IsEmpty) await pipe.WriteAsync(password, ct);
        await pipe.FlushAsync(ct);
    }
}
