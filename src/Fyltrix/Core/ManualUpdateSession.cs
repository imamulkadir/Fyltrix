using System.Net.Http;
using Fyltrix.Launcher;
using Fyltrix.Updates;

namespace Fyltrix.Core;

/// <summary>Shared verified update session. Callers choose when to check; it never writes application settings.</summary>
internal sealed class ManualUpdateSession : IDisposable
{
    private readonly Func<CancellationToken, Task<ReleaseCheck>> check;
    private readonly Func<ReleaseInfo, CancellationToken, Task<ManifestVerification>> verify;
    private readonly Func<ReleaseInfo, IProgress<DownloadProgress>, CancellationToken, Task<DownloadResult>> download;
    private readonly Func<string, CancellationToken, Task<StagedUpdate?>> restore;
    private readonly IDisposable? resource;
    private CancellationTokenSource? operation;
    private bool disposed;
    private bool installing;
    private VerifiedRelease? available;
    internal ReleaseVersion Current { get; }
    internal string? LayoutRoot { get; }
    internal bool Busy => operation != null || installing;
    internal bool Downloading { get; private set; }
    internal bool CanDownload => !disposed && !Busy && LayoutRoot != null && available != null && Stage == null;
    internal bool CanInstall => !disposed && !Busy && LayoutRoot != null && Stage != null;
    internal StagedUpdate? Stage { get; private set; }
    internal DownloadProgress? Progress { get; private set; }
    internal string Message { get; private set; } = "No update check has been performed in this session.";
    internal string Notes => available?.Release.Notes ?? "";
    internal DateTimeOffset? RetryAfter { get; private set; }
    internal DateTimeOffset? LastChecked { get; private set; }
    internal CheckStatus? LastCheckStatus { get; private set; }
    internal string? AvailableVersion => available?.Release.Version.Text;
    internal event Action? Changed;
    internal event Action? CheckCompleted;
    internal event Action? DownloadCompleted;

    internal ManualUpdateSession(ReleaseVersion current, string? layoutRoot,
        Func<CancellationToken, Task<ReleaseCheck>> check,
        Func<ReleaseInfo, CancellationToken, Task<ManifestVerification>> verify,
        Func<ReleaseInfo, IProgress<DownloadProgress>, CancellationToken, Task<DownloadResult>> download,
        IDisposable? resource = null, Func<string, CancellationToken, Task<StagedUpdate?>>? restore = null)
    {
        Current = current; LayoutRoot = layoutRoot; this.check = check; this.verify = verify; this.download = download; this.resource = resource;
        this.restore = restore ?? ((folder, token) => UpdateHandover.LoadStageAsync(folder, token: token));
    }

    internal static ManualUpdateSession Create()
    {
        var current = ReleaseVersion.Parse(Diagnostics.Version) ?? throw new InvalidOperationException("Invalid application version.");
        string? root = UpdateProtocol.PayloadRoot(AppContext.BaseDirectory);
        if (root != null)
        {
            try
            {
                if (!UpdateProtocol.Supported(root) || ReleaseLayout.Resolve(root)?.Executable.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) != true)
                    root = null;
                for (var dir = root == null ? null : new DirectoryInfo(root); dir != null; dir = dir.Parent)
                    if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) { root = null; break; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { root = null; }
        }
        var connection = new Connection();
        return new(current, root, ct => connection.Client.CheckAsync(current, cancellationToken: ct),
            (release, ct) => connection.Client.VerifyManifestAsync(release, cancellationToken: ct),
            (release, progress, ct) => new UpdateDownloader(connection.Client).DownloadAsync(release, current, ReleasePolicy.ZipName(release.Version.Text), progress: progress, token: ct), connection);
    }

    internal async Task CheckAsync()
    {
        if (disposed || Busy) return;
        if (Stage != null) { SetMessage("A verified update is ready. Choose Install and restart when you are ready."); return; }
        if (RetryAfter is { } retry && DateTimeOffset.UtcNow < retry) { SetMessage($"GitHub asked us to wait. Try again after {retry.ToLocalTime():g}."); return; }
        using var cancel = new CancellationTokenSource(); operation = cancel;
        available = null; Stage = null; Progress = null; LastCheckStatus = null; SetMessage("Checking GitHub for a newer release…");
        try
        {
            var result = await check(cancel.Token);
            LastCheckStatus = result.Status;
            cancel.Token.ThrowIfCancellationRequested(); RetryAfter = result.RetryAfter;
            if (result.Status != CheckStatus.Available || result.Release == null)
            {
                SetMessage(result.Status switch
                {
                    CheckStatus.UpToDate => "You are using the latest published version.",
                    CheckStatus.NoRelease => "No published release is available yet.",
                    CheckStatus.RateLimited => RetryAfter is { } time ? $"GitHub rate limit reached. Try after {time.ToLocalTime():g}." : "GitHub rate limit reached. Try again later.",
                    CheckStatus.Timeout => "The update check timed out. Try again later.",
                    _ => "Could not check for updates. Check your connection and try again."
                }); return;
            }
            var authenticated = await verify(result.Release, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (authenticated.Verified == null) { SetMessage("This release could not be authenticated. Nothing was downloaded or installed."); return; }
            if (!authenticated.Verified.Files.Any(f => f.Name == ReleasePolicy.ZipName(result.Release.Version.Text)))
            { SetMessage("This release has no supported update package. Use its manual installer."); return; }
            available = authenticated.Verified; SetMessage($"Version {result.Release.Version.Text} is available. Downloading does not install it.");
        }
        catch (OperationCanceledException) { LastCheckStatus = null; SetMessage("Update check cancelled."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { Diagnostics.Log("Update check", ex); SetMessage("Could not check for updates. Nothing was changed."); }
        finally { LastChecked = DateTimeOffset.UtcNow; operation = null; Changed?.Invoke(); CheckCompleted?.Invoke(); }
    }

    internal async Task DownloadAsync()
    {
        if (!CanDownload || disposed) return;
        using var cancel = new CancellationTokenSource(); operation = cancel;
        Downloading = true; Progress = new(0, 0);
        var release = available!.Release; SetMessage("Downloading and verifying the update…");
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (!disposed && operation == cancel)
            {
                Progress = p;
                SetMessage($"Downloading: {p.Received / 1048576.0:F1} / {p.Total / 1048576.0:F1} MB");
            }
        });
        try
        {
            var result = await download(release, progress, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            Stage = result.Failure == DownloadFailure.None ? result.Staged : null;
            SetMessage(Stage != null ? "Download verified. Finish file operations and close tools before installing." :
                result.Failure switch
                {
                    DownloadFailure.NoSpace => "There is not enough space to download the update. Nothing was installed.",
                    DownloadFailure.Untrusted or DownloadFailure.PackageRejected => "The update failed verification. Nothing was installed.",
                    DownloadFailure.Network or DownloadFailure.TimedOut => "The download could not finish. Check your connection and try again. Nothing was installed.",
                    _ => "The download could not be saved. Check free space and folder access, then try again. Nothing was installed."
                });
        }
        catch (OperationCanceledException) { SetMessage("Download cancelled. Nothing was installed."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { Diagnostics.Log("Update download", ex); SetMessage("Download failed. Nothing was installed."); }
        finally { Downloading = false; operation = null; Changed?.Invoke(); DownloadCompleted?.Invoke(); }
    }
    internal async Task RestoreStageAsync(string folder, CancellationToken token)
    {
        if (disposed || Busy || LayoutRoot == null || Stage != null) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token); operation = cancel;
        SetMessage("Verifying the previously downloaded update…");
        try
        {
            var saved = await Task.Run(() => restore(folder, cancel.Token), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (saved != null && saved.Verified.Release.Version.CompareTo(Current) > 0 && saved.AssetName == ReleasePolicy.ZipName(saved.Verified.Release.Version.Text))
            { Stage = saved; available = saved.Verified; SetMessage("Download verified. Choose Install and restart when you are ready."); }
            else SetMessage("The saved update is unavailable or failed verification. Check for updates to download it again. Nothing was installed.");
        }
        catch (OperationCanceledException) { SetMessage("Saved update verification cancelled."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Diagnostics.Log("Restore downloaded update", ex); SetMessage("The saved update could not be verified. Nothing was installed."); }
        finally { operation = null; Changed?.Invoke(); }
    }
    internal void SetMessage(string message) { Message = message; Changed?.Invoke(); }
    internal bool BeginInstall()
    {
        if (disposed || !CanInstall) return false;
        installing = true; Changed?.Invoke(); return true;
    }
    internal void EndInstall() { installing = false; Changed?.Invoke(); }
    internal void Cancel() => operation?.Cancel();
    internal void RestoreBackoff(DateTimeOffset? retry)
    {
        if (retry is { } time && time > DateTimeOffset.UtcNow) RetryAfter = time;
    }
    public void Dispose() { disposed = true; Cancel(); resource?.Dispose(); }

    private sealed class Connection : IDisposable
    {
        private HttpClient? http;
        private GitHubReleaseClient? client;
        internal GitHubReleaseClient Client => client ??= new(http = GitHubReleaseClient.CreateHttpClient());
        public void Dispose() => http?.Dispose();
    }
}
