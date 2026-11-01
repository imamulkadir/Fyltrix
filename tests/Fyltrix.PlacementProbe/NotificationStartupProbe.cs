using System.IO;
using System.Net.Http;
using System.Text.Json;
using Fyltrix.Core;
using Fyltrix.UI;
using Fyltrix.Updates;

// Test-only startup driver. The real App.Main initializes the scheduler, with no live HTTP.
internal sealed class NotificationStartupProbe(string root, string data)
{
    private int checks, downloads;
    private UpdateNotifications? model;
    private bool dismissed;
    private string? previous;
    internal UpdateNotifications Create(ManualUpdateSession original)
    {
        var release = ReleaseInfo.Parse(File.ReadAllBytes(Path.Combine(data, "release.json")))!;
        byte[] manifest = File.ReadAllBytes(Path.Combine(data, ReleasePolicy.ManifestName));
        byte[] signature = File.ReadAllBytes(Path.Combine(data, ReleasePolicy.SignatureName));
        var session = new ManualUpdateSession(original.Current, original.LayoutRoot,
            _ => { checks++; return Task.FromResult(new ReleaseCheck(CheckStatus.Available, release)); },
            (info, _) => Task.FromResult(SignedManifest.Verify(info, manifest, signature)),
            async (info, progress, token) =>
            {
                downloads++;
                using var http = new HttpClient(new RejectNetwork());
                return await new UpdateDownloader(new GitHubReleaseClient(http)).StageLocalAsync(info, original.Current, manifest, signature,
                    Path.Combine(data, ReleasePolicy.ZipName(info.Version.Text)), ReleasePolicy.PinnedPublicKeys[0], Path.Combine(root, "update-metadata"), progress, token);
            });
        model = new UpdateNotifications(session, new UpdateNotificationStore(Path.Combine(root, "update-metadata")),
            wait: (duration, token) => Task.Delay(duration == ApplicationDefaults.UpdateStartupDelay ? TimeSpan.FromSeconds(2) : duration, token));
        return model;
    }
    internal void Tick(MainWindow window)
    {
        if (model == null) return;
        if (!dismissed && File.Exists(Path.Combine(root, "dismiss-notice"))) { dismissed = true; model.Dismiss(); }
        string temporary = Path.Combine(root, "notice-probe.tmp");
        string snapshot = JsonSerializer.Serialize(new
        {
            checks, downloads, banner = window.UpdateBannerVisibility.ToString(), window.UpdateBannerText,
            status = window.SettingsHostFor().ReadUpdateStatus!(),
            popupCount = window.OwnedWindows.Count, mode = window.Settings.UpdateMode.ToString(),
            canInstall = model.Session.CanInstall, busy = model.Session.Busy, downloading = model.Session.Downloading, stage = model.Session.Stage?.Directory
        });
        if (snapshot == previous) return;
        try
        {
            File.WriteAllText(temporary, snapshot);
            File.Move(temporary, Path.Combine(root, "notice-probe.json"), true);
            previous = snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* The Python reader may briefly hold the prior result; retry this test record next tick. */ }
    }
    private sealed class RejectNetwork : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("No live HTTP in startup integration tests."); }
}
