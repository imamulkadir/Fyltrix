using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

internal static class SigningKeySetup
{
    internal const string PrivateName = "fyltrix-release-private.pem";
    internal const string PublicName = "fyltrix-release-public.txt";
    internal const string PasswordName = "backup-recovery-password.txt";
    internal const string BackupName = "fyltrix-release-private.encrypted.pem";

    [SupportedOSPlatform("windows")]
    internal static object Create(string primary, string backup)
    {
        primary = NewDirectory(primary); backup = NewDirectory(backup);
        if (Within(primary, backup) || Within(backup, primary)) throw new ArgumentException("Signing and backup directories must be separate.");
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Missing Windows account.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        // Apply a protected ACL at directory creation, before writing any secret bytes.
        CreateProtected(primary, user, system); CreateProtected(backup, user, system);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var parameters = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);
        string encrypted = key.ExportEncryptedPkcs8PrivateKeyPem(password, parameters);
        WriteNew(Path.Combine(primary, PrivateName), key.ExportPkcs8PrivateKeyPem());
        WriteNew(Path.Combine(primary, PasswordName), password);
        WriteNew(Path.Combine(primary, PublicName), publicKey);
        WriteNew(Path.Combine(backup, BackupName), encrypted);
        WriteNew(Path.Combine(backup, PublicName), publicKey);
        WriteNew(Path.Combine(backup, "RESTORE.txt"),
            "Fyltrix ECDSA P-256 release signing backup. Encrypted PKCS#8, AES-256-CBC/PBKDF2-SHA256.\r\n" +
            "Keep the recovery password separately in your private password manager/offline recovery storage.\r\n" +
            "The password is initially in the protected local signing directory, not this backup.\r\n" +
            "Copy the encrypted backup to independent offline storage; a second local folder does not survive loss of this PC.\r\n" +
            "Never commit the private key or password, upload them to GitHub, or paste them into chat.\r\n");
        using var restored = ECDsa.Create();
        restored.ImportFromEncryptedPem(File.ReadAllText(Path.Combine(backup, BackupName)), File.ReadAllText(Path.Combine(primary, PasswordName)));
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        byte[] signature = restored.SignData(challenge, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (Convert.ToBase64String(restored.ExportSubjectPublicKeyInfo()) != publicKey ||
            !key.VerifyData(challenge, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Backup verification failed. Retain generated files for review.");
        foreach (string folder in new[] { primary, backup })
            foreach (string file in Directory.EnumerateFiles(folder)) RequirePrivateAcl(new FileInfo(file).GetAccessControl(), user, system);
        return new { publicKey, fingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant(),
            primaryDirectory = primary, backupDirectory = backup, encryptedBackupVerified = true, offlineBackupVerified = false };
    }

    private static bool Within(string child, string parent) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string NewDirectory(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("Use a local absolute signing directory.");
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (path == Path.GetPathRoot(path) || File.Exists(path) || Directory.Exists(path)) throw new IOException("Signing destinations must be new and will never be overwritten.");
        foreach (var parent in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            if (Within(path, Path.Combine(parent, "FileLens"))) throw new ArgumentException("Existing application data is protected.");
        for (var info = new DirectoryInfo(path); info != null; info = info.Parent)
        {
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Signing paths cannot traverse links.");
            if (File.Exists(Path.Combine(info.FullName, ".git")) || Directory.Exists(Path.Combine(info.FullName, ".git"))) throw new IOException("Signing keys must be outside every Git checkout.");
            if (File.Exists(Path.Combine(info.FullName, ".fyltrix-install.ini")) || File.Exists(Path.Combine(info.FullName, "Fyltrix.exe"))) throw new IOException("Signing keys must be separate from application copies.");
        }
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("Create the parent signing/backup folder first.");
        return path;
    }
    [SupportedOSPlatform("windows")]
    private static void CreateProtected(string path, SecurityIdentifier user, SecurityIdentifier system)
    {
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(user);
        foreach (var sid in new[] { user, system }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(acl);
        RequirePrivateAcl(new DirectoryInfo(path).GetAccessControl(), user, system);
    }
    [SupportedOSPlatform("windows")]
    private static void RequirePrivateAcl(FileSystemSecurity acl, SecurityIdentifier user, SecurityIdentifier system)
    {
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(user) && !rule.IdentityReference.Equals(system))
                throw new UnauthorizedAccessException("Signing permissions are broader than the current account and SYSTEM.");
    }
    private static void WriteNew(string path, string text)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(Encoding.UTF8.GetBytes(text)); file.Flush(flushToDisk: true);
    }
}
