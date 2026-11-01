using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;
using Fyltrix.UI;
using Fyltrix.Updates;

namespace Fyltrix.Tests;

[Collection("UI")]
public sealed class AutomaticDownloadTests
{
    // A small, genuinely signed layout ZIP: nothing executable is run or installed.
    internal sealed class Feed : IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly HttpClient http = new(new RejectNetwork());
        internal string PublicKey => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        internal readonly byte[] Manifest, Signature;
        internal readonly ReleaseInfo Release;
        internal readonly string Package;
        internal readonly string Root;
        internal int Downloads;
        internal Feed(TempDir temp)
        {
            Root = temp["updates"];
            Package = temp[ReleasePolicy.ZipName("0.2.0")];
            using (var zip = ZipFile.Open(Package, ZipArchiveMode.Create))
            {
                void Add(string name, string body) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(body); }
                Add("Fyltrix.exe", "test-only launcher"); Add("app-0.2.0/Fyltrix.exe", "test-only payload");
                Add("current.json", "{\"current\":\"0.2.0\",\"previous\":null}");
            }
            byte[] bytes = File.ReadAllBytes(Package);
            Manifest = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, product = "Fyltrix", version = "0.2.0",
                files = new[] { new { name = Path.GetFileName(Package), size = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() } } });
            Signature = System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(Manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
            Release = ReleaseInfo.Parse(JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v0.2.0", html_url = "https://github.com/imamulkadir/Fyltrix/releases/tag/v0.2.0", draft = false, prerelease = false,
                assets = new[] { (Path.GetFileName(Package), bytes.Length), (ReleasePolicy.ManifestName, Manifest.Length), (ReleasePolicy.SignatureName, Signature.Length) }
                    .Select(x => new { name = x.Item1, size = x.Item2, state = "uploaded", browser_download_url = "https://github.com/imamulkadir/Fyltrix/releases/download/v0.2.0/" + x.Item1 }) }))!;
        }
        internal Task<DownloadResult> Download(ReleaseInfo release, IProgress<DownloadProgress> progress, CancellationToken token)
        {
            Downloads++;
            return new UpdateDownloader(new GitHubReleaseClient(http)).StageLocalAsync(release, ReleaseVersion.Parse("0.1.0")!, Manifest, Signature, Package, PublicKey, Root, progress, token);
        }
        internal ManualUpdateSession Session(Func<ReleaseInfo, IProgress<DownloadProgress>, CancellationToken, Task<DownloadResult>>? download = null, bool supported = true, bool trusted = true, string current = "0.1.0") =>
            new(ReleaseVersion.Parse(current)!, supported ? "D:\\synthetic-layout" : null,
                _ => Task.FromResult(new ReleaseCheck(CheckStatus.Available, Release)),
                (r, _) => Task.FromResult(trusted ? SignedManifest.Verify(r, Manifest, Signature, [PublicKey]) : new ManifestVerification(VerificationFailure.InvalidSignature)),
                download ?? Download, restore: (folder, token) => UpdateHandover.LoadStageAsync(folder, [PublicKey], token));
        public void Dispose() { key.Dispose(); http.Dispose(); }
        private sealed class RejectNetwork : HttpMessageHandler
        { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("No live HTTP in these tests."); }
    }

    [Fact]
    public async Task Automatic_download_is_verified_persisted_and_never_installed_or_saved_to_preferences()
    {
        using var temp = new TempDir(); using var feed = new Feed(temp);
        string settings = temp.File("settings.json", "unrelated original preferences"); var original = File.ReadAllBytes(settings);
        using var session = feed.Session(); var store = new UpdateNotificationStore(feed.Root);
        using var model = new UpdateNotifications(session, store);
        model.Configure(UpdateMode.DownloadAutomatically); await session.CheckAsync(); await model.DownloadTask; await model.PendingSave;
        Assert.Equal(1, feed.Downloads); Assert.NotNull(session.Stage); Assert.True(session.CanInstall);
        Assert.Equal("0.2.0", model.BannerVersion); Assert.Equal(Path.GetFileName(session.Stage.Directory), store.Load().PendingStage);
        Assert.Equal(original, File.ReadAllBytes(settings));
        Assert.False(File.Exists(Path.Combine(session.Stage.Directory, "handover.json")));
        Assert.False(File.Exists(Path.Combine(session.Stage.Directory, "close-approved")));
        var stage = session.Stage; await session.CheckAsync(); Assert.Same(stage, session.Stage);
        model.Configure(UpdateMode.ManualOnly); await model.Loop;
    }
    [Theory]
    [InlineData(false, true)] [InlineData(true, false)]
    public async Task Unsupported_or_untrusted_updates_never_download(bool supported, bool trusted)
    {
        using var temp = new TempDir(); using var feed = new Feed(temp); using var session = feed.Session(supported: supported, trusted: trusted);
        using var model = new UpdateNotifications(session, new UpdateNotificationStore(feed.Root));
        model.Configure(UpdateMode.DownloadAutomatically); await session.CheckAsync(); await model.DownloadTask;
        Assert.Equal(0, feed.Downloads); Assert.False(session.CanInstall);
    }
    [Theory]
    [InlineData(DownloadFailure.NoSpace)] [InlineData(DownloadFailure.PackageRejected)] [InlineData(DownloadFailure.Network)]
    public async Task Download_failure_does_not_install_or_retry_in_a_tight_loop(DownloadFailure failure)
    {
        using var temp = new TempDir(); using var feed = new Feed(temp); int calls = 0;
        using var session = feed.Session((_, _, _) => { calls++; return Task.FromResult(new DownloadResult(failure)); });
        using var model = new UpdateNotifications(session, new UpdateNotificationStore(feed.Root));
        model.Configure(UpdateMode.DownloadAutomatically); await session.CheckAsync(); await model.DownloadTask;
        model.Configure(UpdateMode.DownloadAutomatically); Assert.Equal(1, calls); Assert.Null(session.Stage); Assert.False(session.CanInstall);
        Assert.Contains("Nothing was installed", session.Message);
        await session.DownloadAsync(); Assert.Equal(2, calls); // Explicit retry remains possible.
    }
    [Fact]
    public async Task Enabling_download_uses_the_already_verified_release_once()
    {
        using var temp = new TempDir(); using var feed = new Feed(temp);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = feed.Session((r, p, t) => { started.SetResult(); return feed.Download(r, p, t); });
        using var model = new UpdateNotifications(session, new UpdateNotificationStore(feed.Root));
        model.Configure(UpdateMode.NotifyOnly); await session.CheckAsync(); Assert.Equal(0, feed.Downloads);
        model.Configure(UpdateMode.DownloadAutomatically); await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await model.DownloadTask; await model.PendingSave;
        model.Configure(UpdateMode.DownloadAutomatically); Assert.Equal(1, feed.Downloads); Assert.True(session.Stage != null, session.Message);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Opting_out_or_closing_cancels_the_automatic_download_and_rejects_late_success(bool close)
    {
        using var temp = new TempDir(); using var feed = new Feed(temp);
        var late = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken downloadToken = default;
        using var session = feed.Session((_, _, token) => { downloadToken = token; return late.Task; });
        using var model = new UpdateNotifications(session, new UpdateNotificationStore(feed.Root));
        model.Configure(UpdateMode.DownloadAutomatically); await session.CheckAsync(); Assert.True(session.Downloading);
        if (close) model.Dispose(); else model.Configure(UpdateMode.NotifyOnly);
        Assert.True(downloadToken.IsCancellationRequested);
        var ready = await feed.Download(feed.Release, new Progress<DownloadProgress>(), default); late.SetResult(ready);
        await model.DownloadTask; Assert.Null(session.Stage); Assert.False(session.CanInstall);
        model.Configure(UpdateMode.ManualOnly); await model.Loop;
    }
    [Fact]
    public async Task Preference_changes_do_not_cancel_a_manually_requested_download()
    {
        using var temp = new TempDir(); using var feed = new Feed(temp); CancellationToken downloadToken = default;
        var late = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = feed.Session((_, _, token) => { downloadToken = token; return late.Task; });
        using var model = new UpdateNotifications(session, new UpdateNotificationStore(feed.Root));
        model.Configure(UpdateMode.NotifyOnly); await session.CheckAsync(); var manual = session.DownloadAsync();
        model.Configure(UpdateMode.ManualOnly); await model.Loop;
        Assert.False(downloadToken.IsCancellationRequested); late.SetResult(new(DownloadFailure.Network)); await manual;
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Restart_reauthenticates_download_and_refuses_tampered_bytes(bool corrupt)
    {
        using var temp = new TempDir(); using var feed = new Feed(temp); var store = new UpdateNotificationStore(feed.Root); string package;
        using (var session = feed.Session())
        using (var model = new UpdateNotifications(session, store))
        {
            model.Configure(UpdateMode.DownloadAutomatically); await session.CheckAsync(); await model.DownloadTask; await model.PendingSave;
            package = session.Stage!.PackagePath; model.Dismiss(); model.Configure(UpdateMode.ManualOnly); await model.Loop;
        }
        if (corrupt) { byte[] bytes = File.ReadAllBytes(package); bytes[^1] ^= 1; File.WriteAllBytes(package, bytes); }
        using var restarted = feed.Session(); using var next = new UpdateNotifications(restarted, store);
        next.Configure(UpdateMode.ManualOnly); await next.Loop; await next.PendingSave;
        Assert.Equal(!corrupt, restarted.CanInstall); Assert.Equal(1, feed.Downloads); Assert.Null(next.BannerVersion);
        if (corrupt) { Assert.Null(store.Load().PendingStage); Assert.Contains("failed verification", restarted.Message); }
        Assert.True(File.Exists(package)); // Never deletes unknown or modified staged data.
    }
    [Theory]
    [InlineData("..\\outside")] [InlineData("C:\\outside")] [InlineData("stage-invalid")]
    public async Task Stored_stage_names_cannot_redirect_restore(string name)
    {
        using var temp = new TempDir(); var store = new UpdateNotificationStore(temp.Path);
        store.Save(new(PendingStage: name)); using var feed = new Feed(temp); using var session = feed.Session();
        using var model = new UpdateNotifications(session, store); model.Configure(UpdateMode.ManualOnly); await model.Loop; await model.PendingSave;
        Assert.Null(session.Stage); Assert.Null(store.Load().PendingStage);
    }
    [Fact]
    public async Task Development_copy_does_not_forget_an_installed_copys_ready_download()
    {
        using var temp = new TempDir(); using var feed = new Feed(temp); var store = new UpdateNotificationStore(feed.Root);
        var ready = (await feed.Download(feed.Release, new Progress<DownloadProgress>(), default)).Staged!;
        string name = Path.GetFileName(ready.Directory); store.Save(new(PendingStage: name));
        using var session = feed.Session(supported: false); using var model = new UpdateNotifications(session, store);
        model.Configure(UpdateMode.ManualOnly); await model.Loop; await model.PendingSave;
        Assert.Null(session.Stage); Assert.Equal(name, store.Load().PendingStage); Assert.True(File.Exists(ready.PackagePath));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Settings_shows_progress_and_can_cancel_or_offer_verified_manual_installation(bool cancel)
    {
        using var temp = new TempDir(); temp.File("file.txt", "test"); using var feed = new Feed(temp);
        Sta.Run(() =>
        {
            var owner = MainWindowUiTests.OpenWindow(temp, temp.Path);
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var session = feed.Session(async (release, progress, token) =>
            { progress.Report(new(4, 10)); await blocked.Task.WaitAsync(token); return await feed.Download(release, progress, token); });
            using var model = new UpdateNotifications(session, new UpdateNotificationStore(feed.Root));
            try
            {
                owner.Settings.UpdateMode = UpdateMode.DownloadAutomatically; owner.Settings.ShowHidden = true; owner.StartUpdateNotifications(model);
                var check = session.CheckAsync(); Sta.WaitUntil(() => check.IsCompleted && session.Downloading, "automatic download starts");
                var settings = new SettingsWindow(owner, owner.Settings, templates: [], host: owner.SettingsHostFor());
                Sta.ShowOffscreen(settings); settings.SelectTab("Updates"); Sta.DoEvents(100);
                Assert.Equal(Visibility.Visible, settings.CancelUpdateDownloadButton.Visibility); Assert.Contains("Downloading", settings.UpdatesStatus.Text);
                Assert.True(settings.TrySave()); owner.ApplyUpdatePreferences(); Assert.True(session.Downloading); Assert.True(owner.Settings.ShowHidden);
                Assert.Equal(UpdateMode.DownloadAutomatically, AppSettings.Load().UpdateMode);
                Sta.Render(settings, "automatic-update-downloading");
                if (cancel) settings.CancelUpdateDownloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else blocked.SetResult();
                Sta.WaitUntil(() => model.DownloadTask.IsCompleted, "automatic download completed or cancelled");
                Sta.DoEvents(100);
                Assert.Equal(!cancel, session.CanInstall);
                Assert.Equal(Visibility.Collapsed, settings.CancelUpdateDownloadButton.Visibility);
                if (cancel) Assert.Contains("cancelled", settings.UpdatesStatus.Text);
                else
                {
                    Assert.Contains("ready to install", owner.UpdateBannerText);
                    Assert.False(File.Exists(Path.Combine(session.Stage!.Directory, "handover.json")));
                    Sta.Render(owner, "automatic-update-ready");
                }
                settings.Close();
                if (!cancel)
                {
                    owner.PresentUpdatesForTest = Sta.ShowOffscreen;
                    var dialog = owner.OpenUpdates(); Assert.True(dialog.InstallButton.IsEnabled); Assert.False(dialog.CheckButton.IsEnabled);
                    dialog.ConfirmInstallForTest = () => false;
                    Assert.True(dialog.InstallAsync().IsCompletedSuccessfully);
                    Assert.True(owner.IsLoaded); Assert.True(session.CanInstall);
                    Assert.False(File.Exists(Path.Combine(session.Stage!.Directory, "handover.json")));
                    dialog.Close();
                }
                Sta.WaitUntil(() => model.PendingSave.IsCompleted, "ready history saved");
            }
            finally { owner.Close(); }
        });
    }
}
