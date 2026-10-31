using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Fyltrix.Launcher;
using Fyltrix.Updates;

namespace Fyltrix.Updates.Tests;

public sealed class ApplyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Fyltrix-ApplyTests-" + Guid.NewGuid().ToString("N"));
    private readonly Fixture fixture = new();
    private string Layout => Path.Combine(root, "layout");
    private string Profile => Path.Combine(root, "profile");
    public ApplyTests() => Directory.CreateDirectory(root);
    public void Dispose() { fixture.Dispose(); Directory.Delete(root, true); }
    private async Task<(ReadyHandover Ready, string Request)> Prepare()
    {
        Directory.CreateDirectory(Path.Combine(Layout, "app-0.1.0")); Directory.CreateDirectory(Profile);
        await File.WriteAllTextAsync(Path.Combine(Profile, "settings.json"), "{\"ShowHidden\":true,\"Favourites\":[\"X:\\\\personal\"]}");
        await File.WriteAllBytesAsync(Path.Combine(Layout, "Fyltrix.exe"), Fixture.Pe());
        await File.WriteAllBytesAsync(Path.Combine(Layout, "app-0.1.0/Fyltrix.exe"), Fixture.Pe());
        await File.WriteAllTextAsync(Path.Combine(Layout, "current.json"), "{\"current\":\"0.1.0\",\"previous\":null}");
        await File.WriteAllTextAsync(Path.Combine(Layout, UpdateProtocol.CapabilityFile), UpdateProtocol.Capability);
        await File.WriteAllBytesAsync(Path.Combine(Layout, UpdateProtocol.GateFile), []);
        await File.WriteAllTextAsync(Path.Combine(Layout, ".fyltrix-install.ini"), "[Installation]\nSchema=1\nAppId=Fyltrix.Sandbox.PerUser\nMode=Sandbox\n");
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] value) { using var stream = zip.CreateEntry(name).Open(); stream.Write(value); }
            Add("Fyltrix.exe", Fixture.Pe()); Add("app-0.2.0/Fyltrix.exe", Fixture.Pe());
            Add("app-0.2.0/test.data", Encoding.UTF8.GetBytes("candidate data"));
            Add("current.json", Encoding.UTF8.GetBytes("{\"current\":\"0.2.0\",\"previous\":null}"));
            Add(UpdateProtocol.CapabilityFile, Encoding.UTF8.GetBytes(UpdateProtocol.Capability)); Add(UpdateProtocol.GateFile, []);
        }
        fixture.Name = ReleasePolicy.ZipName(Fixture.Version); fixture.Package = bytes.ToArray();
        string input = Path.Combine(root, fixture.Name); await File.WriteAllBytesAsync(input, fixture.Package);
        byte[] manifest = fixture.Manifest, sig = fixture.Signature(manifest);
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("Offline only")));
        var staged = (await new UpdateDownloader(new GitHubReleaseClient(http)).StageLocalAsync(fixture.Release(manifest, sig), ReleaseVersion.Parse("0.1.0")!,
            manifest, sig, input, fixture.PublicKey, Path.Combine(root, "staging"))).Staged!;
        var request = new HandoverRequest(1, staged.Token, Layout, "0.1.0", "0.2.0", fixture.Name, [new(123, 456)]);
        string path = Path.Combine(staged.Directory, UpdateHandover.RequestName); await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(request));
        await File.WriteAllTextAsync(Path.Combine(staged.Directory, UpdateHandover.ApprovalName), staged.Token);
        return (new ReadyHandover(staged.Verified, request, File.OpenRead(staged.PackagePath)), path);
    }
    private ApplyOptions Options(string mode = "healthy", Action<ApplyPoint>? checkpoint = null) => new()
    {
        Sandbox = true, Profile = Profile, HealthTimeout = TimeSpan.FromSeconds(2), Restart = _ => { }, Checkpoint = checkpoint,
        Start = original =>
        {
            string probe = Path.ChangeExtension(Assembly.Load("Fyltrix.HandoverProbe").Location, ".exe");
            var info = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--trial-test");
            foreach (var pair in original.Environment) info.Environment[pair.Key] = pair.Value;
            info.Environment["FYLTRIX_TEST_HEALTH_MODE"] = mode;
            return Process.Start(info)!;
        }
    };
    [Theory]
    [InlineData("healthy", ApplyStatus.Committed)] [InlineData("mutate-trial", ApplyStatus.Committed)]
    [InlineData("crash", ApplyStatus.RolledBack)] [InlineData("timeout", ApplyStatus.RolledBack)] [InlineData("wrong-token", ApplyStatus.RolledBack)]
    public async Task Health_controls_commit_and_live_data_is_never_restored_or_rewritten(string mode, ApplyStatus expected)
    {
        var prepared = await Prepare(); using var ready = prepared.Ready;
        byte[] profile = await File.ReadAllBytesAsync(Path.Combine(Profile, "settings.json"));
        byte[] pointer = await File.ReadAllBytesAsync(Path.Combine(Layout, "current.json"));
        var result = await UpdateApplier.ApplyCoreAsync(ready, prepared.Request, Options(mode), CancellationToken.None);
        Assert.Equal(expected, result.Status);
        Assert.Equal(profile, await File.ReadAllBytesAsync(Path.Combine(Profile, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Layout, "app-0.1.0/Fyltrix.exe")));
        if (expected == ApplyStatus.Committed)
        { Assert.Equal("0.2.0", ReleaseLayout.Resolve(Layout)!.Version); Assert.Equal("0.1.0", ReleaseLayout.ParsePointer(await File.ReadAllBytesAsync(Path.Combine(Layout, "current.json")))!.Previous); }
        else Assert.Equal(pointer, await File.ReadAllBytesAsync(Path.Combine(Layout, "current.json")));
        Assert.False(File.Exists(Path.Combine(Layout, UpdateProtocol.JournalFile)));
    }
    [Theory]
    [InlineData("JournalWritten")] [InlineData("PayloadExtracted")] [InlineData("TrialPassed")] [InlineData("PointerSwitched")]
    public async Task Failures_before_commit_restore_the_exact_old_pointer(string name)
    {
        var point = Enum.Parse<ApplyPoint>(name);
        var prepared = await Prepare(); using var ready = prepared.Ready;
        byte[] pointer = await File.ReadAllBytesAsync(Path.Combine(Layout, "current.json"));
        var result = await UpdateApplier.ApplyCoreAsync(ready, prepared.Request, Options(checkpoint: p => { if (p == point) throw new IOException("Injected disk failure"); }), CancellationToken.None);
        Assert.Equal(ApplyStatus.RolledBack, result.Status);
        Assert.Equal(pointer, await File.ReadAllBytesAsync(Path.Combine(Layout, "current.json")));
    }
    [Fact]
    public async Task Interrupted_switch_blocks_startup_until_authenticated_recovery()
    {
        var prepared = await Prepare(); using var ready = prepared.Ready;
        byte[] pointer = await File.ReadAllBytesAsync(Path.Combine(Layout, "current.json"));
        await Assert.ThrowsAsync<PowerLoss>(() => UpdateApplier.ApplyCoreAsync(ready, prepared.Request,
            Options(checkpoint: p => { if (p == ApplyPoint.PointerSwitched) throw new PowerLoss(); }), CancellationToken.None));
        Assert.Throws<IOException>(() => UpdateProtocol.EnterShared(Layout));
        Assert.Equal(ApplyStatus.Refused, (await UpdateRecovery.RecoverAsync(prepared.Request, ready.Request.Token, null, Options(), CancellationToken.None)).Status);
        var result = await UpdateRecovery.RecoverAsync(prepared.Request, ready.Request.Token, [fixture.PublicKey], Options(), CancellationToken.None);
        Assert.Equal(ApplyStatus.RolledBack, result.Status);
        Assert.Equal(pointer, await File.ReadAllBytesAsync(Path.Combine(Layout, "current.json")));
        using var lease = UpdateProtocol.EnterShared(Layout); Assert.NotNull(lease);
    }
    [Fact]
    public async Task A_committed_but_interrupted_cleanup_keeps_the_new_version()
    {
        var prepared = await Prepare(); using var ready = prepared.Ready;
        await Assert.ThrowsAsync<PowerLoss>(() => UpdateApplier.ApplyCoreAsync(ready, prepared.Request,
            Options(checkpoint: p => { if (p == ApplyPoint.Committed) throw new PowerLoss(); }), CancellationToken.None));
        var result = await UpdateRecovery.RecoverAsync(prepared.Request, ready.Request.Token, [fixture.PublicKey], Options(), CancellationToken.None);
        Assert.Equal(ApplyStatus.Committed, result.Status); Assert.Equal("0.2.0", ReleaseLayout.Resolve(Layout)!.Version);
    }
    [Fact]
    public async Task Existing_payloads_and_busy_startup_gates_are_refused_without_overwriting()
    {
        var prepared = await Prepare(); using var ready = prepared.Ready;
        using (var lease = UpdateProtocol.EnterShared(Layout))
            Assert.Equal(ApplyStatus.Refused, (await UpdateApplier.ApplyCoreAsync(ready, prepared.Request, Options(), CancellationToken.None)).Status);
        Directory.CreateDirectory(Path.Combine(Layout, "app-0.2.0")); await File.WriteAllTextAsync(Path.Combine(Layout, "app-0.2.0/user.keep"), "keep");
        Assert.Equal(ApplyStatus.Refused, (await UpdateApplier.ApplyCoreAsync(ready, prepared.Request, Options(), CancellationToken.None)).Status);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(Layout, "app-0.2.0/user.keep")));
    }
    private sealed class PowerLoss : Exception { }
    [Fact]
    public async Task A_locked_profile_aborts_before_changing_code_or_creating_a_journal()
    {
        var prepared = await Prepare(); using var ready = prepared.Ready;
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(Layout, "current.json"));
        using var locked = new FileStream(Path.Combine(Profile, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await UpdateApplier.ApplyCoreAsync(ready, prepared.Request, Options(), CancellationToken.None);
        Assert.Equal(ApplyStatus.Refused, result.Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(Layout, "current.json")));
        Assert.False(File.Exists(Path.Combine(Layout, UpdateProtocol.JournalFile)));
    }
    [Fact]
    public async Task Live_data_changes_are_preserved_and_prevent_commit()
    {
        var prepared = await Prepare(); using var ready = prepared.Ready;
        var result = await UpdateApplier.ApplyCoreAsync(ready, prepared.Request,
            Options(checkpoint: p => { if (p == ApplyPoint.TrialPassed) File.WriteAllText(Path.Combine(Profile, "new-user-data.keep"), "keep"); }), CancellationToken.None);
        Assert.Equal(ApplyStatus.RolledBack, result.Status);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(Profile, "new-user-data.keep")));
        Assert.Equal("0.1.0", ReleaseLayout.Resolve(Layout)!.Version);
    }
    [Fact]
    public async Task Recovery_refuses_changed_committed_files_or_a_user_edited_pointer()
    {
        var prepared = await Prepare(); using var ready = prepared.Ready;
        await Assert.ThrowsAsync<PowerLoss>(() => UpdateApplier.ApplyCoreAsync(ready, prepared.Request,
            Options(checkpoint: p => { if (p == ApplyPoint.Committed) throw new PowerLoss(); }), CancellationToken.None));
        await File.WriteAllTextAsync(Path.Combine(Layout, "app-0.2.0/test.data"), "user edit");
        Assert.Equal(ApplyStatus.Refused, (await UpdateRecovery.RecoverAsync(prepared.Request, ready.Request.Token, [fixture.PublicKey], Options(), CancellationToken.None)).Status);
        Assert.True(File.Exists(Path.Combine(Layout, UpdateProtocol.JournalFile)));
    }
}
