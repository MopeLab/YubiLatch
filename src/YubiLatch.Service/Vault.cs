using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using YubiLatch.Core;

namespace YubiLatch.Service;

internal sealed class Vault
{
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MopeLab", "YubiLatch");

    public Vault()
    {
        if (!Directory.Exists(_root)) throw new InvalidOperationException("Installer must create the protected vault directory.");
        CheckDirectory();
    }

    private void CheckDirectory()
    {
        for (DirectoryInfo? node = new(_root); node != null; node = node.Parent)
            if ((node.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Reparse points are not allowed.");
        var acl = new DirectoryInfo(_root).GetAccessControl();
        if (!acl.AreAccessRulesProtected) throw new UnauthorizedAccessException("Vault inheritance must be disabled.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner != system && owner != admins) throw new UnauthorizedAccessException("Untrusted vault owner.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference != system && rule.IdentityReference != admins)
                throw new UnauthorizedAccessException("Untrusted vault access rule.");
    }

    private string FileFor(string sid)
    {
        _ = new SecurityIdentifier(sid);
        return Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))) + ".vault");
    }

    private static byte[] Entropy(string sid) => SHA256.HashData(Encoding.UTF8.GetBytes("YubiLatch/v1/" + sid));

    public AccountRecord? Load(string sid)
    {
        CheckDirectory();
        var path = FileFor(sid);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is <= 0 or > 131072)
            throw new InvalidDataException("Invalid vault file.");
        var cipher = File.ReadAllBytes(path);
        byte[]? plain = null;
        AccountRecord? account = null;
        try
        {
            plain = ProtectedData.Unprotect(cipher, Entropy(sid), DataProtectionScope.CurrentUser);
            account = JsonSerializer.Deserialize<AccountRecord>(plain, Wire.Json) ?? throw new InvalidDataException();
            account.Validate(sid);
            return account;
        }
        catch { account?.Dispose(); throw; }
        finally { if (plain != null) CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save(AccountRecord account)
    {
        CheckDirectory();
        account.Validate(account.Sid);
        var plain = JsonSerializer.SerializeToUtf8Bytes(account, Wire.Json);
        byte[] cipher;
        try { cipher = ProtectedData.Protect(plain, Entropy(account.Sid), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var path = FileFor(account.Sid);
        var temp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(cipher);
                file.Flush(true);
            }
            // Never keep/restore an older vault automatically: that could resurrect a revoked key.
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Delete(string sid)
    {
        CheckDirectory();
        File.Delete(FileFor(sid));
    }
}
