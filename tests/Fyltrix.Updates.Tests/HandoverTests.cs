using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Fyltrix.Updates;

namespace Fyltrix.Updates.Tests;

public sealed class HandoverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Fyltrix-HandoverTests-" + Guid.NewGuid().ToString("N"));
    private readonly Fixture fixture = new();
    private readonly List<Process> processes = [];
    private string Layout => Path.Combine(root, "layout & unicode বাংলা");
    public HandoverTests() => Directory.CreateDirectory(root);
    public void Dispose()
    {
        foreach (var process in processes)
        {
            if (!process.HasExited) { process.StandardInput.WriteLine("exit"); process.StandardInput.Flush(); }
            Assert.True(process.WaitForExit(10_000)); process.Dispose();
        }
        fixture.Dispose(); Directory.Delete(root, true);
    }
    private async Task<StagedUpdate> Stage()
    {
        string file = Path.Combine(root, fixture.Name); await File.WriteAllBytesAsync(file, fixture.Package);
        byte[] manifest = fixture.Manifest, sig = fixture.Signature(manifest);
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("Offline test must not use HTTP")));
        var result = await new UpdateDownloader(new GitHubReleaseClient(http)).StageLocalAsync(fixture.Release(manifest, sig),
            ReleaseVersion.Parse("0.1.0")!, manifest, sig, file, fixture.PublicKey, Path.Combine(root, "staging"));
        Assert.Equal(DownloadFailure.None, result.Failure); return result.Staged!;
    }
    private async Task<Process> Start(string directory)
    {
        Directory.CreateDirectory(directory);
        string assembly = Assembly.Load("Fyltrix.HandoverProbe").Location;
        string source = Path.GetDirectoryName(assembly)!;
        foreach (string file in new[] { "Fyltrix.HandoverProbe.dll", "Fyltrix.HandoverProbe.deps.json", "Fyltrix.HandoverProbe.runtimeconfig.json", "Fyltrix.Bootstrap.dll" })
            File.Copy(Path.Combine(source, file), Path.Combine(directory, file));
        File.Copy(Path.ChangeExtension(assembly, ".exe"), Path.Combine(directory, "Fyltrix.exe"));
        var info = new ProcessStartInfo(Path.Combine(directory, "Fyltrix.exe")) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--test-wait");
        var process = Process.Start(info)!; processes.Add(process);
        Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        return process;
    }
    private async Task<PreparationResult> Prepare(StagedUpdate stage, bool launcher = true)
    {
        await Start(Path.Combine(Layout, "app-0.1.0"));
        if (launcher) await Start(Layout);
        else File.WriteAllText(Path.Combine(Layout, "Fyltrix.exe"), "root launcher fixture");
        await File.WriteAllTextAsync(Path.Combine(Layout, "current.json"), "{\"current\":\"0.1.0\",\"previous\":null}");
        return await UpdateHandover.PrepareAsync(stage, Layout);
    }
    [Fact]
    public async Task Close_cancellation_never_approves_handover_and_keeps_download()
    {
        var stage = await Stage(); var prepared = await Prepare(stage);
        Assert.Equal(HandoverFailure.None, prepared.Failure);
        Assert.Equal(2, prepared.Preparation!.Request.Processes.Length);
        Assert.Equal(HandoverFailure.CloseCancelled, await UpdateHandover.RequestCloseAsync(prepared.Preparation, () => false));
        Assert.False(File.Exists(prepared.Preparation.RequestPath));
        Assert.False(File.Exists(Path.Combine(stage.Directory, UpdateHandover.ApprovalName)));
        Assert.True(File.Exists(stage.PackagePath)); Assert.All(processes, p => Assert.False(p.HasExited));
    }
    [Fact]
    public async Task A_running_launcher_or_payload_is_never_terminated_or_treated_as_ready()
    {
        var stage = await Stage(); var prepared = (await Prepare(stage)).Preparation!;
        var result = await UpdateHandover.WaitForReadyAsync(prepared.RequestPath, TimeSpan.FromMilliseconds(100), explicitTestKeys: [fixture.PublicKey]);
        Assert.Equal(HandoverFailure.StillRunning, result.Failure); Assert.Null(result.Ready);
        Assert.All(processes, p => Assert.False(p.HasExited));
        // Closing only the payload must still wait for the root launcher.
        processes[0].StandardInput.WriteLine("exit"); processes[0].StandardInput.Flush();
        await processes[0].WaitForExitAsync();
        result = await UpdateHandover.WaitForReadyAsync(prepared.RequestPath, TimeSpan.FromMilliseconds(100), explicitTestKeys: [fixture.PublicKey]);
        Assert.Equal(HandoverFailure.StillRunning, result.Failure); Assert.False(processes[1].HasExited);
    }
    [Fact]
    public async Task Approved_handover_waits_for_all_processes_then_returns_a_locked_read_lease()
    {
        var stage = await Stage(); var prepared = (await Prepare(stage)).Preparation!;
        var initial = await UpdateHandover.WaitForReadyAsync(prepared.RequestPath, TimeSpan.FromMilliseconds(100));
        Assert.Equal(HandoverFailure.Untrusted, initial.Failure);
        Assert.Equal(HandoverFailure.None, await UpdateHandover.RequestCloseAsync(prepared, () =>
        {
            foreach (var process in processes) { process.StandardInput.WriteLine("exit"); process.StandardInput.Flush(); }
            return true;
        }));
        var ready = await UpdateHandover.WaitForReadyAsync(prepared.RequestPath, explicitTestKeys: [fixture.PublicKey]);
        Assert.Equal(HandoverFailure.None, ready.Failure);
        using (ready.Ready)
        {
            Assert.NotNull(ready.Ready);
            Assert.False(ready.Ready.Package.CanWrite);
            Assert.Equal(0, ready.Ready.Package.Position);
            Assert.Throws<IOException>(() => File.OpenWrite(stage.PackagePath).Dispose());
            Assert.Equal("{\"current\":\"0.1.0\",\"previous\":null}", await File.ReadAllTextAsync(Path.Combine(Layout, "current.json")));
        }
        Assert.Equal(fixture.Package, await File.ReadAllBytesAsync(stage.PackagePath));
    }
    [Fact]
    public async Task A_cancelled_wait_leaves_all_application_processes_alive()
    {
        var stage = await Stage(); var prepared = (await Prepare(stage)).Preparation!;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateHandover.WaitForReadyAsync(prepared.RequestPath,
            token: cancel.Token, explicitTestKeys: [fixture.PublicKey]));
        Assert.All(processes, p => Assert.False(p.HasExited));
    }
    [Fact]
    public async Task Cancellation_during_the_close_callback_cannot_approve_handover()
    {
        var stage = await Stage(); var prepared = (await Prepare(stage)).Preparation!;
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateHandover.RequestCloseAsync(prepared,
            () => { cancel.Cancel(); return true; }, cancel.Token));
        Assert.False(File.Exists(prepared.RequestPath));
        Assert.False(File.Exists(Path.Combine(stage.Directory, UpdateHandover.ApprovalName)));
    }
    [Fact]
    public async Task An_unremovable_cancelled_request_stays_unapproved_even_after_process_exit()
    {
        var stage = await Stage(); var prepared = (await Prepare(stage)).Preparation!;
        using (var locked = new FileStream(prepared.RequestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(HandoverFailure.CloseCancelled, await UpdateHandover.RequestCloseAsync(prepared, () => false));
            Assert.True(File.Exists(prepared.RequestPath));
            Assert.False(File.Exists(Path.Combine(stage.Directory, UpdateHandover.ApprovalName)));
        }
        foreach (var process in processes) { process.StandardInput.WriteLine("exit"); process.StandardInput.Flush(); }
        var result = await UpdateHandover.WaitForReadyAsync(prepared.RequestPath, explicitTestKeys: [fixture.PublicKey]);
        Assert.Equal(HandoverFailure.CloseCancelled, result.Failure); Assert.Null(result.Ready);
    }
    [Theory]
    [InlineData("package")] [InlineData("manifest")] [InlineData("pointer")] [InlineData("identity")]
    public async Task Changed_handover_inputs_are_refused_before_readiness(string corruption)
    {
        var stage = await Stage(); var prepared = (await Prepare(stage)).Preparation!;
        if (corruption == "package") { byte[] bytes = fixture.Package.ToArray(); bytes[^1] ^= 1; await File.WriteAllBytesAsync(stage.PackagePath, bytes); }
        if (corruption == "manifest") await File.WriteAllTextAsync(Path.Combine(stage.Directory, ReleasePolicy.ManifestName), "{}");
        if (corruption == "pointer") await File.WriteAllTextAsync(Path.Combine(Layout, "current.json"), "{\"current\":\"0.1.1\"}");
        if (corruption == "identity")
        {
            var request = prepared.Request with { Processes = prepared.Request.Processes.Select(p => p with { StartUtcTicks = p.StartUtcTicks + 1 }).ToArray() };
            await File.WriteAllBytesAsync(prepared.RequestPath, JsonSerializer.SerializeToUtf8Bytes(request));
        }
        var result = await UpdateHandover.WaitForReadyAsync(prepared.RequestPath, TimeSpan.FromMilliseconds(100), explicitTestKeys: [fixture.PublicKey]);
        Assert.NotEqual(HandoverFailure.None, result.Failure); Assert.Null(result.Ready);
        Assert.All(processes, p => Assert.False(p.HasExited));
    }
    [Fact]
    public async Task No_running_payload_and_existing_request_are_not_overwritten()
    {
        var stage = await Stage();
        Directory.CreateDirectory(Path.Combine(Layout, "app-0.1.0"));
        await File.WriteAllTextAsync(Path.Combine(Layout, "Fyltrix.exe"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(Layout, "app-0.1.0/Fyltrix.exe"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(Layout, "current.json"), "{\"current\":\"0.1.0\"}");
        Assert.Equal(HandoverFailure.NoRunningPayload, (await UpdateHandover.PrepareAsync(stage, Layout)).Failure);
        File.Delete(Path.Combine(Layout, "Fyltrix.exe")); File.Delete(Path.Combine(Layout, "app-0.1.0/Fyltrix.exe"));
        var prepared = await Prepare(stage);
        Assert.Equal(HandoverFailure.None, prepared.Failure);
        byte[] before = await File.ReadAllBytesAsync(prepared.Preparation!.RequestPath);
        Assert.Equal(HandoverFailure.CouldNotPrepare, (await UpdateHandover.PrepareAsync(stage, Layout)).Failure);
        Assert.Equal(before, await File.ReadAllBytesAsync(prepared.Preparation.RequestPath));
    }
}
