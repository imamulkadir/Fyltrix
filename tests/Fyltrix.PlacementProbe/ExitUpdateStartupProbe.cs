using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Fyltrix.Core;
using Fyltrix.UI;
using Fyltrix.Updates;

// Offline Sandbox-only driver; explicit disposable trust never changes the shipped pins.
internal sealed class ExitUpdateStartupProbe(string root, string data)
{
    private UpdateNotifications? model;
    private bool configured, requested;
    private string? previous;
    private string Mode => Environment.GetEnvironmentVariable("FYLTRIX_EXIT_UPDATE_CASE") ?? "commit";
    internal UpdateNotifications Create(ManualUpdateSession original)
    {
        var release = ReleaseInfo.Parse(File.ReadAllBytes(Path.Combine(data, "release.json")))!;
        byte[] manifest = File.ReadAllBytes(Path.Combine(data, ReleasePolicy.ManifestName)), signature = File.ReadAllBytes(Path.Combine(data, ReleasePolicy.SignatureName));
        string key = File.ReadAllText(Path.Combine(data, "public-test.txt")).Trim();
        File.WriteAllText(Path.Combine(root, "public-test.txt"), key);
        var session = new ManualUpdateSession(original.Current, original.LayoutRoot,
            _ => Task.FromResult(release.Version.CompareTo(original.Current) > 0 ? new ReleaseCheck(CheckStatus.Available, release) : new ReleaseCheck(CheckStatus.UpToDate)),
            (info, _) => Task.FromResult(SignedManifest.Verify(info, manifest, signature, [key])),
            async (info, progress, token) =>
            {
                using var http = new HttpClient(new RejectNetwork());
                return await new UpdateDownloader(new GitHubReleaseClient(http)).StageLocalAsync(info, original.Current, manifest, signature,
                    Path.Combine(data, ReleasePolicy.ZipName(info.Version.Text)), key, Path.Combine(root, "staging"), progress, token);
            }, restore: (folder, token) => UpdateHandover.LoadStageAsync(folder, [key], token));
        model = new UpdateNotifications(session, new UpdateNotificationStore(Path.Combine(root, "staging")),
            wait: (duration, token) => Task.Delay(duration == ApplicationDefaults.UpdateStartupDelay ? TimeSpan.FromSeconds(2) : duration, token));
        return model;
    }
    internal void Tick(MainWindow window)
    {
        if (model == null) return;
        if (!configured)
        {
            configured = true;
            if (Mode == "cancel-close") window.Closing += (_, args) => { if (window.UpdateWorkerRunning && !File.Exists(Path.Combine(root, "release-app"))) args.Cancel = true; };
            window.StartUpdateWorkerForTest = info =>
            {
                File.WriteAllText(Path.Combine(root, "worker-options.json"), JsonSerializer.Serialize(new { args = info.ArgumentList.ToArray() }));
                if (Mode == "worker-start-failure") throw new Win32Exception("Injected worker-start refusal.");
                info.Environment["FYLTRIX_SANDBOX_UPDATE_WORKER"] = "1";
                if (Mode == "health-failure") info.Environment["FYLTRIX_SANDBOX_FAULT"] = "invalid-trial-settings";
                if (Mode == "manual") info.Environment["FYLTRIX_MANUAL_UPDATE_CASE"] = "commit";
                var process = Process.Start(info)!;
                File.WriteAllText(Path.Combine(root, "worker-pid.json"), JsonSerializer.Serialize(new { pid = process.Id }));
                return Wait(process);
                static async Task Wait(Process process) { using (process) await process.WaitForExitAsync(); }
            };
        }
        if (!requested && model.Session.CanInstall && File.Exists(Path.Combine(root, "request-exit")))
        {
            requested = true;
            if (Mode == "manual")
            {
                var dialog = window.OpenUpdates(); dialog.ConfirmInstallForTest = () => true; _ = dialog.InstallAsync();
            }
            else window.Close();
        }
        string snapshot = JsonSerializer.Serialize(new { ready = model.Session.CanInstall, stage = model.Session.Stage?.Directory,
            model.Session.Message, pending = !window.ExitUpdateTask.IsCompleted, worker = window.UpdateWorkerRunning, window.Settings.UpdateMode });
        if (snapshot == previous) return;
        try { File.WriteAllText(Path.Combine(root, "exit-status.tmp"), snapshot); File.Move(Path.Combine(root, "exit-status.tmp"), Path.Combine(root, "exit-status.json"), true); previous = snapshot; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private sealed class RejectNetwork : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Offline Sandbox tests must never contact HTTP."); }
}
