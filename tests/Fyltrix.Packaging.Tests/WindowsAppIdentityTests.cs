using Fyltrix.Launcher;

namespace Fyltrix.Packaging.Tests;

public sealed class WindowsAppIdentityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Fyltrix-identity-" + Guid.NewGuid().ToString("N"), "space & unicode é");
    public WindowsAppIdentityTests() => Directory.CreateDirectory(root);
    private void Marker(string id, string mode) => File.WriteAllText(Path.Combine(root, ".fyltrix-install.ini"),
        $"[Installation]\r\nSchema=1\r\nAppId={id}\r\nMode={mode}\r\nVersionPatch=6\r\n");

    [Theory]
    [InlineData("Fyltrix.PerUser", "Production", WindowsAppIdentity.ProductionId)]
    [InlineData("Fyltrix.Sandbox.PerUser", "Sandbox", WindowsAppIdentity.SandboxId)]
    public void Installed_identity_and_relaunch_stay_at_root_after_update(string installerId, string mode, string expected)
    {
        Marker(installerId, mode);
        File.WriteAllText(Path.Combine(root, "Fyltrix.exe"), "launcher");
        File.WriteAllText(Path.Combine(root, "current.json"), "{}");
        foreach (string version in new[] { "0.1.6", "0.1.7", "1.0.0" })
        {
            string payload = Path.Combine(root, "app-" + version);
            Directory.CreateDirectory(payload);
            var identity = WindowsAppIdentity.ForPayload(payload);
            Assert.Equal(expected, identity.Id);
            Assert.Equal(WindowsAppIdentity.ForRoot(root), identity);
            Assert.Equal("\"" + Path.Combine(root, "Fyltrix.exe") + "\"", identity.RelaunchCommand);
            Assert.DoesNotContain("app-", identity.RelaunchCommand);
        }
    }

    [Fact]
    public void Portable_and_development_copies_are_distinct_from_install_and_each_other()
    {
        var portable = WindowsAppIdentity.ForRoot(root);
        Assert.StartsWith("Fyltrix.Desktop.Portable.", portable.Id);
        Assert.Equal(portable, WindowsAppIdentity.ForRoot(root + Path.DirectorySeparatorChar));
        Assert.Equal(portable.Id, WindowsAppIdentity.ForRoot(root.ToUpperInvariant()).Id);
        Assert.NotEqual(portable.Id, WindowsAppIdentity.ForRoot(root + "-other").Id);
        Marker("Fyltrix.PerUser", "Production");
        var dev = WindowsAppIdentity.ForRoot(root, development: true);
        Assert.StartsWith("Fyltrix.Desktop.Development.", dev.Id);
        Assert.NotEqual(portable.Id, dev.Id);
        Assert.Equal(WindowsAppIdentity.ProductionId, WindowsAppIdentity.ForRoot(root).Id);
    }

    [Theory]
    [InlineData("[Installation]\nSchema=1\nAppId=Fyltrix.PerUser\nMode=Sandbox")]
    [InlineData("[Other]\nSchema=1\nAppId=Fyltrix.PerUser\nMode=Production")]
    [InlineData("[Installation]\nSchema=2\nAppId=Fyltrix.PerUser\nMode=Production")]
    [InlineData("[Installation]\nSchema=1\nAppId=Fyltrix.PerUser\nMode=Production\nAppId=other")]
    public void Malformed_markers_do_not_claim_installed_identity(string marker)
    {
        File.WriteAllText(Path.Combine(root, ".fyltrix-install.ini"), marker);
        Assert.StartsWith("Fyltrix.Desktop.Portable.", WindowsAppIdentity.ForRoot(root).Id);
    }

    [Fact]
    public void Installer_shortcut_contract_matches_runtime_identity()
    {
        string? repo = AppContext.BaseDirectory;
        while (repo != null && !File.Exists(Path.Combine(repo, "packaging", "installer", "Fyltrix.iss"))) repo = Path.GetDirectoryName(repo.TrimEnd(Path.DirectorySeparatorChar));
        // Search from the working tree when tests use a separate output directory.
        repo ??= Environment.CurrentDirectory;
        string source = File.ReadAllText(Path.Combine(repo, "packaging", "installer", "Fyltrix.iss"));
        Assert.Contains("#define TaskbarId \"" + WindowsAppIdentity.ProductionId + "\"", source);
        Assert.Contains("#define TaskbarId \"" + WindowsAppIdentity.SandboxId + "\"", source);
        foreach (string line in source.Split('\n').Where(line => line.StartsWith("Name: \"{userprograms}") || line.StartsWith("Name: \"{userdesktop}")))
        {
            Assert.Contains("Filename: \"{app}\\Fyltrix.exe\"", line);
            Assert.Contains("AppUserModelID: \"{#TaskbarId}\"", line);
        }
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
}
