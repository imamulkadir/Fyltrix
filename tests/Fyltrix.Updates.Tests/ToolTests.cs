using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Fyltrix.Updates;

namespace Fyltrix.Updates.Tests;

public sealed class ToolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Fyltrix-UpdateToolTests-" + Guid.NewGuid().ToString("N"));
    public ToolTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add(Assembly.Load("Fyltrix.UpdateTool").Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output + await error);
    }
    [Fact]
    public async Task Production_signing_refuses_an_unpinned_key_without_writing_outputs()
    {
        using var fixture = new Fixture();
        string key = Path.Combine(root, "release test.pem"), pub = Path.Combine(root, "public release test.txt"),
            setup = Path.Combine(root, fixture.Name), signed = Path.Combine(root, "production-signature");
        await File.WriteAllTextAsync(key, fixture.Key.ExportPkcs8PrivateKeyPem());
        await File.WriteAllTextAsync(pub, fixture.PublicKey);
        await File.WriteAllBytesAsync(setup, fixture.Package);
        var result = await Run("sign-release", Fixture.Version, key, pub, signed, setup);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not pinned", result.Output);
        Assert.False(Directory.Exists(signed));
        Assert.Equal(fixture.Package, await File.ReadAllBytesAsync(setup));
    }
    [Fact]
    public async Task Signing_setup_creates_a_private_key_and_restorable_encrypted_backup_without_printing_secrets()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows key setup requires Windows.");
        string primary = Path.Combine(root, "signing & unicode বাংলা"), backup = Path.Combine(root, "encrypted backup");
        var result = await Run("create-signing-key", primary, backup);
        Assert.True(result.ExitCode == 0, result.Output);
        var info = JsonNode.Parse(result.Output)!;
        Assert.True(info["encryptedBackupVerified"]!.GetValue<bool>());
        Assert.False(info["offlineBackupVerified"]!.GetValue<bool>());
        string password = await File.ReadAllTextAsync(Path.Combine(primary, "backup-recovery-password.txt"));
        Assert.DoesNotContain(password, result.Output);
        Assert.DoesNotContain("PRIVATE KEY", result.Output);
        using var restored = System.Security.Cryptography.ECDsa.Create();
        restored.ImportFromEncryptedPem(await File.ReadAllTextAsync(Path.Combine(backup, "fyltrix-release-private.encrypted.pem")), password);
        Assert.Equal(info["publicKey"]!.GetValue<string>(), Convert.ToBase64String(restored.ExportSubjectPublicKeyInfo()));
        Assert.False(File.Exists(Path.Combine(backup, "backup-recovery-password.txt")));
        var acl = System.IO.FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(primary));
        Assert.True(acl.AreAccessRulesProtected);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
            Assert.True(rule.IdentityReference.Equals(user) || rule.IdentityReference.Equals(system));
        var again = await Run("create-signing-key", primary, Path.Combine(root, "new backup"));
        Assert.Equal(2, again.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(root, "new backup")));
        Assert.Equal(password, await File.ReadAllTextAsync(Path.Combine(primary, "backup-recovery-password.txt")));
    }
    [Fact]
    public async Task Signing_setup_refuses_existing_backup_or_overlapping_destinations_without_writing_a_key()
    {
        string primary = Path.Combine(root, "never-created");
        var existing = await Run("create-signing-key", primary, root);
        Assert.Equal(2, existing.ExitCode); Assert.False(Directory.Exists(primary));
        var same = await Run("create-signing-key", primary, primary);
        Assert.Equal(2, same.ExitCode); Assert.False(Directory.Exists(primary));
        var checkout = Path.Combine(root, "checkout"); Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        var insideGit = await Run("create-signing-key", Path.Combine(checkout, "keys"), Path.Combine(root, "safe backup"));
        Assert.Equal(2, insideGit.ExitCode); Assert.False(Directory.Exists(Path.Combine(checkout, "keys")));
        Assert.False(Directory.Exists(Path.Combine(root, "safe backup")));
    }
    [Fact]
    public async Task Signing_tool_and_offline_verifier_interoperate_and_preserve_inputs()
    {
        using var fixture = new Fixture();
        string key = Path.Combine(root, "private test.pem"), pub = Path.Combine(root, "public test.txt"),
            setup = Path.Combine(root, fixture.Name), signed = Path.Combine(root, "signed output & test"), feed = Path.Combine(root, "release.json");
        await File.WriteAllTextAsync(key, fixture.Key.ExportPkcs8PrivateKeyPem());
        await File.WriteAllTextAsync(pub, fixture.PublicKey);
        await File.WriteAllBytesAsync(setup, fixture.Package);
        var sign = await Run("sign", Fixture.Version, key, pub, signed, setup);
        Assert.True(sign.ExitCode == 0, sign.Output);
        string manifestPath = Path.Combine(signed, ReleasePolicy.ManifestName), sigPath = Path.Combine(signed, ReleasePolicy.SignatureName);
        byte[] data = await File.ReadAllBytesAsync(manifestPath), sig = await File.ReadAllBytesAsync(sigPath);
        await File.WriteAllBytesAsync(feed, Fixture.Bytes(fixture.Metadata(data, sig)));
        var check = await Run("check", "0.1.0", feed);
        Assert.True(check.ExitCode == 0, check.Output);
        Assert.Equal("Available", JsonNode.Parse(check.Output)!["status"]!.GetValue<string>());
        var verify = await Run("verify", feed, manifestPath, sigPath, setup, pub);
        Assert.True(verify.ExitCode == 0, verify.Output);
        Assert.Equal("None", JsonNode.Parse(verify.Output)!["verification"]!.GetValue<string>());
        Assert.False(JsonNode.Parse(verify.Output)!["installed"]!.GetValue<bool>());
        Assert.Equal(fixture.Package, await File.ReadAllBytesAsync(setup));
        var staged = await Run("stage-local", "0.1.0", feed, manifestPath, sigPath, setup, pub, Path.Combine(root, "offline staging"));
        Assert.True(staged.ExitCode == 0, staged.Output);
        var stageFolder = JsonNode.Parse(staged.Output)!["directory"]!.GetValue<string>();
        Assert.Equal(fixture.Package, await File.ReadAllBytesAsync(Path.Combine(stageFolder, fixture.Name)));
        Assert.False(JsonNode.Parse(staged.Output)!["installed"]!.GetValue<bool>());
        var repeat = await Run("sign", Fixture.Version, key, pub, signed, setup);
        Assert.NotEqual(0, repeat.ExitCode);
        Assert.Equal(data, await File.ReadAllBytesAsync(manifestPath));
        byte[] corrupt = fixture.Package.ToArray(); corrupt[^1] ^= 1;
        await File.WriteAllBytesAsync(setup, corrupt);
        Assert.NotEqual(0, (await Run("verify", feed, manifestPath, sigPath, setup, pub)).ExitCode);
        using var wrong = new Fixture(); await File.WriteAllTextAsync(pub, wrong.PublicKey);
        Assert.NotEqual(0, (await Run("sign", Fixture.Version, key, pub, Path.Combine(root, "wrong-key-output"), setup)).ExitCode);
        Assert.False(Directory.Exists(Path.Combine(root, "wrong-key-output")));
    }
}
