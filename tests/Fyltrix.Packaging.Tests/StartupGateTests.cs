using Fyltrix.Launcher;

namespace Fyltrix.Packaging.Tests;

public sealed class StartupGateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Fyltrix-GateTests-" + Guid.NewGuid().ToString("N"));
    public StartupGateTests() { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, UpdateProtocol.CapabilityFile), UpdateProtocol.Capability); File.WriteAllBytes(Path.Combine(root, UpdateProtocol.GateFile), []); }
    public void Dispose() => Directory.Delete(root, true);
    [Fact]
    public void Shared_startups_exclude_a_writer_and_release_cleanly()
    {
        using (var first = UpdateProtocol.EnterShared(root))
        using (var second = UpdateProtocol.EnterShared(root))
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(root, UpdateProtocol.GateFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        using var writer = new FileStream(Path.Combine(root, UpdateProtocol.GateFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => UpdateProtocol.EnterShared(root));
    }
    [Fact]
    public void Interrupted_update_blocks_startup_without_leaking_the_gate()
    {
        File.WriteAllText(Path.Combine(root, UpdateProtocol.JournalFile), "pending");
        Assert.Throws<IOException>(() => UpdateProtocol.EnterShared(root));
        using var writer = new FileStream(Path.Combine(root, UpdateProtocol.GateFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Fact]
    public void Readonly_gate_is_usable_for_normal_startup_and_legacy_layout_is_unchanged()
    {
        File.SetAttributes(Path.Combine(root, UpdateProtocol.GateFile), FileAttributes.ReadOnly);
        try { using var lease = UpdateProtocol.EnterShared(root); Assert.NotNull(lease); }
        finally { File.SetAttributes(Path.Combine(root, UpdateProtocol.GateFile), FileAttributes.Normal); }
        File.Delete(Path.Combine(root, UpdateProtocol.CapabilityFile));
        Assert.Null(UpdateProtocol.EnterShared(root));
    }
}
