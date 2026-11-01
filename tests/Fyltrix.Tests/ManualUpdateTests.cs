using System.Security.Cryptography;
using System.Text.Json;
using Fyltrix.Core;
using Fyltrix.UI;
using Fyltrix.Updates;

namespace Fyltrix.Tests;

[Collection("UI")]
public sealed class ManualUpdateTests
{
    [Fact]
    public void Development_copy_uses_its_current_version_and_requires_manual_installation()
    {
        using var session = ManualUpdateSession.Create();
        Assert.Equal(Diagnostics.Version, session.Current.Text);
        Assert.Null(session.LayoutRoot); Assert.False(session.Busy);
        Assert.False(session.CanDownload); Assert.False(session.CanInstall);
    }
    [Fact]
    public void Worker_notifications_require_the_exact_bounded_successful_close_token()
    {
        using var temp = new TempDir(); string nonce = Guid.NewGuid().ToString("N");
        Assert.False(App.ApprovedUpdateNotification(temp.Path, nonce));
        temp.File("close-approved", nonce); Assert.True(App.ApprovedUpdateNotification(temp.Path, nonce));
        Assert.False(App.ApprovedUpdateNotification(temp.Path, Guid.NewGuid().ToString("N")));
        File.WriteAllText(temp["close-approved"], nonce + new string('x', 100_000));
        Assert.False(App.ApprovedUpdateNotification(temp.Path, nonce));
    }
    internal sealed class Feed : IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal ReleaseInfo Release { get; }
        internal ManifestVerification Verification { get; }
        internal Feed()
        {
            string name = "Fyltrix-0.2.0-win-x64.zip";
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, product = "Fyltrix", version = "0.2.0", files = new[] { new { name, size = 10, sha256 = new string('a', 64) } } });
            byte[] signature = System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
            byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v0.2.0", html_url = "https://github.com/imamulkadir/Fyltrix/releases/tag/v0.2.0", draft = false, prerelease = false, body = "<script>plain text only</script>",
                assets = new[] { (name, 10), (ReleasePolicy.ManifestName, manifest.Length), (ReleasePolicy.SignatureName, signature.Length) }.Select(a => new { name = a.Item1, size = a.Item2, state = "uploaded", browser_download_url = "https://github.com/imamulkadir/Fyltrix/releases/download/v0.2.0/" + a.Item1 }) });
            Release = ReleaseInfo.Parse(metadata)!;
            Verification = SignedManifest.Verify(Release, manifest, signature, new[] { Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
            Assert.NotNull(Verification.Verified);
        }
        internal ManualUpdateSession Session(string? root = "D:\\synthetic-layout", Func<CancellationToken, Task<ReleaseCheck>>? check = null,
            Func<ReleaseInfo, CancellationToken, Task<ManifestVerification>>? verify = null,
            Func<ReleaseInfo, IProgress<DownloadProgress>, CancellationToken, Task<DownloadResult>>? download = null) =>
            new(ReleaseVersion.Parse("0.1.0")!, root, check ?? (_ => Task.FromResult(new ReleaseCheck(CheckStatus.Available, Release))),
                verify ?? ((_, _) => Task.FromResult(Verification)), download ?? ((_, _, _) => Task.FromResult(new DownloadResult(DownloadFailure.InvalidResponse))));
        public void Dispose() => key.Dispose();
    }
    [Fact]
    public async Task Opening_a_session_is_inert_and_download_requires_an_authenticated_release()
    {
        using var feed = new Feed(); int checks = 0, downloads = 0;
        using var session = feed.Session(check: _ => { checks++; return Task.FromResult(new ReleaseCheck(CheckStatus.Available, feed.Release)); },
            download: (_, _, _) => { downloads++; return Task.FromResult(new DownloadResult(DownloadFailure.InvalidResponse)); });
        Assert.Equal(0, checks); Assert.False(session.CanDownload); Assert.False(session.CanInstall);
        await session.DownloadAsync(); Assert.Equal(0, downloads);
        await session.CheckAsync(); Assert.Equal(1, checks); Assert.True(session.CanDownload); Assert.False(session.CanInstall);
        await session.DownloadAsync(); Assert.Equal(1, downloads); Assert.False(session.CanInstall);
        Assert.Contains("Nothing was installed", session.Message);
    }
    [Fact]
    public async Task Untrusted_releases_are_not_offered_and_portable_copies_cannot_download_or_install()
    {
        using var feed = new Feed();
        using var untrusted = feed.Session(verify: (_, _) => Task.FromResult(new ManifestVerification(VerificationFailure.InvalidSignature)));
        await untrusted.CheckAsync(); Assert.False(untrusted.CanDownload); Assert.Empty(untrusted.Notes);
        using var portable = feed.Session(root: null);
        await portable.CheckAsync(); Assert.False(portable.CanDownload); Assert.False(portable.CanInstall);
        Assert.Contains("<script>", portable.Notes); // plain text, never HTML execution
    }
    [Fact]
    public async Task Rate_limits_are_respected_without_automatic_retries()
    {
        using var feed = new Feed(); int calls = 0;
        using var session = feed.Session(check: _ => { calls++; return Task.FromResult(new ReleaseCheck(CheckStatus.RateLimited, RetryAfter: DateTimeOffset.UtcNow.AddMinutes(2))); });
        await session.CheckAsync(); await session.CheckAsync(); Assert.Equal(1, calls); Assert.False(session.CanDownload);
    }
    [Fact]
    public async Task Cancellation_blocks_duplicate_checks_and_releases_busy_state()
    {
        using var feed = new Feed(); int calls = 0;
        using var session = feed.Session(check: async ct => { calls++; await Task.Delay(Timeout.Infinite, ct); return new ReleaseCheck(CheckStatus.NoRelease); });
        var running = session.CheckAsync(); Assert.True(session.Busy);
        await session.CheckAsync(); Assert.Equal(1, calls);
        session.Cancel(); await running; Assert.False(session.Busy); Assert.Contains("cancelled", session.Message);
    }
    [Fact]
    public async Task Cancelled_download_ignores_late_completion_and_allows_an_explicit_retry()
    {
        using var feed = new Feed();
        var completed = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var session = feed.Session(download: (_, _, _) => { calls++; return completed.Task; });
        await session.CheckAsync();
        var download = session.DownloadAsync();
        Assert.True(session.Busy);
        await session.DownloadAsync(); Assert.Equal(1, calls);
        session.Cancel(); completed.SetResult(new DownloadResult(DownloadFailure.None));
        await download;
        Assert.False(session.Busy); Assert.Null(session.Stage); Assert.False(session.CanInstall);
        Assert.True(session.CanDownload); Assert.Contains("cancelled", session.Message);
        session.Dispose(); Assert.False(session.CanDownload); Assert.False(session.CanInstall);
    }
    [Theory]
    [InlineData(CheckStatus.NoRelease)] [InlineData(CheckStatus.UpToDate)] [InlineData(CheckStatus.Network)] [InlineData(CheckStatus.Timeout)]
    public async Task Checks_with_no_new_release_never_enable_installation(CheckStatus status)
    {
        using var feed = new Feed(); using var session = feed.Session(check: _ => Task.FromResult(new ReleaseCheck(status)));
        await session.CheckAsync(); Assert.False(session.CanDownload); Assert.False(session.CanInstall); Assert.False(session.Busy);
    }
    [Fact]
    public void Update_window_does_not_check_on_open_and_closing_it_cancels_inflight_work()
    {
        using var temp = new TempDir(); temp.File("file.txt", "test"); using var feed = new Feed(); int calls = 0;
        Sta.Run(() =>
        {
            var owner = MainWindowUiTests.OpenWindow(temp, temp.Path);
            owner.PresentUpdatesForTest = Sta.ShowOffscreen;
            using var session = feed.Session(check: async ct => { calls++; await Task.Delay(Timeout.Infinite, ct); return new ReleaseCheck(CheckStatus.NoRelease); });
            try
            {
                var window = owner.OpenUpdates(session);
                Assert.Equal(0, calls); Assert.True(window.CheckButton.IsEnabled); Assert.False(window.InstallButton.IsEnabled);
                Sta.DoEvents(100);
                Sta.Render(window, "manual-updates-inert");
                var content = (System.Windows.FrameworkElement)window.Content;
                foreach (System.Windows.Controls.Button button in window.Buttons.Children)
                {
                    var bounds = button.TransformToAncestor(content).TransformBounds(new System.Windows.Rect(button.RenderSize));
                    Assert.True(bounds.Left >= 0 && bounds.Right <= content.ActualWidth, "Update buttons must fit the dialog.");
                }
                var check = session.CheckAsync(); Assert.False(window.CheckButton.IsEnabled);
                window.Close(); Sta.WaitUntil(() => check.IsCompleted, "cancelled update check");
                Assert.False(session.Busy); Assert.True(owner.IsLoaded); Assert.True(owner.TryCloseForUpdate());
            }
            finally { owner.Close(); }
        });
    }
}
