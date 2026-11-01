using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;
using Fyltrix.UI;
using Fyltrix.Updates;

// Test-only private-desktop driver of the real update window. Never shipped with Fyltrix.
internal static class ManualUpdateStartupProbe
{
    internal static Action<MainWindow> Create(string root)
    {
        ManualUpdateSession? session = null; UpdateWindow? dialog = null;
        bool checkedRelease = false, downloaded = false, installed = false;
        string mode = Environment.GetEnvironmentVariable("FYLTRIX_MANUAL_UPDATE_CASE") ?? "commit";
        void Record(string name, object value) => File.WriteAllText(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(value));
        return window =>
        {
            try
            {
                if (session == null && File.Exists(Path.Combine(root, "open-updates")))
                {
                    string data = Environment.GetEnvironmentVariable("FYLTRIX_MANUAL_UPDATE_DATA")!;
                    byte[] manifest = File.ReadAllBytes(Path.Combine(data, ReleasePolicy.ManifestName)), signature = File.ReadAllBytes(Path.Combine(data, ReleasePolicy.SignatureName));
                    var release = ReleaseInfo.Parse(File.ReadAllBytes(Path.Combine(data, "release.json")))!;
                    string publicKey = File.ReadAllText(Path.Combine(root, "public-test.txt")).Trim();
                    if (!ReleasePolicy.PinnedPublicKeys.Contains(publicKey)) throw new InvalidDataException("The integration must use actual production trust.");
                    var http = new HttpClient(new RejectNetwork()); var client = new GitHubReleaseClient(http);
                    session = new ManualUpdateSession(ReleaseVersion.Parse(Diagnostics.Version)!, Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName,
                        _ => Task.FromResult(new ReleaseCheck(CheckStatus.Available, release)),
                        (info, _) => Task.FromResult(SignedManifest.Verify(info, manifest, signature)),
                        (info, progress, token) => new UpdateDownloader(client).StageLocalAsync(info, ReleaseVersion.Parse(Diagnostics.Version)!, manifest, signature,
                            Path.Combine(data, ReleasePolicy.ZipName(info.Version.Text)), publicKey, Path.Combine(root, "staging"), progress, token), http);
                    window.PresentUpdatesForTest = view => { dialog = view; view.ConfirmInstallForTest = () => true; view.ShowActivated = false; view.Show(); };
                    window.StartUpdateWorkerForTest = info =>
                    {
                        if (mode == "worker-start-failure") throw new Win32Exception("Injected worker-start refusal.");
                        info.Environment["FYLTRIX_SANDBOX_UPDATE_WORKER"] = "1";
                        info.Environment["FYLTRIX_TEST_PRODUCTION_TRUST"] = "1";
                        if (mode == "health-failure") info.Environment["FYLTRIX_SANDBOX_FAULT"] = "invalid-trial-settings";
                        var process = Process.Start(info)!;
                        Record("ui-worker", new { pid = process.Id });
                        return Wait(process);
                        static async Task Wait(Process process) { using (process) await process.WaitForExitAsync(); }
                    };
                    if (mode == "cancel-close") window.Closing += (_, e) => { if (!File.Exists(Path.Combine(root, "release-app"))) e.Cancel = true; };
                    window.OpenUpdates(session); Record("ui-opened", new { noAutomaticCheck = !session.Busy && !session.CanDownload });
                }
                if (session == null || dialog == null) return;
                if (!checkedRelease && File.Exists(Path.Combine(root, "check-update"))) { checkedRelease = true; dialog.CheckButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                if (checkedRelease && session.CanDownload && !downloaded)
                {
                    Record("ui-available", new { authenticated = true, notes = session.Notes });
                    if (File.Exists(Path.Combine(root, "download-update"))) { downloaded = true; dialog.DownloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                }
                if (downloaded && session.CanInstall && !installed)
                {
                    Record("ui-downloaded", new { stage = session.Stage!.Directory, noAutomaticInstallation = true });
                    if (File.Exists(Path.Combine(root, "install-update"))) { installed = true; dialog.InstallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                }
                if (installed && mode != "commit" && dialog.IsLoaded && !session.Busy)
                    Record("ui-refused", new { mainStillOpen = window.IsLoaded, session.Message });
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(root, "manual-probe-error.txt"), ex.ToString()); }
        };
    }
    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException("This harness is offline.");
    }
}
