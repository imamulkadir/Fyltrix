using System.Net.Http;
using Fyltrix.Launcher;
using Fyltrix.Updates;

namespace Fyltrix.Core;

/// <summary>One explicitly opened update session. No startup check, scheduler or settings writes.</summary>
internal sealed class ManualUpdateSession : IDisposable
{
    private readonly Func<CancellationToken, Task<ReleaseCheck>> check;
    private readonly Func<ReleaseInfo, CancellationToken, Task<ManifestVerification>> verify;
    private readonly Func<ReleaseInfo, IProgress<DownloadProgress>, CancellationToken, Task<DownloadResult>> download;
    private readonly IDisposable? resource;
    private CancellationTokenSource? operation;
    private bool disposed;
    private bool installing;
    private VerifiedRelease? available;
    internal ReleaseVersion Current { get; }
    internal string? LayoutRoot { get; }
    internal bool Busy => operation != null || installing;
    internal bool CanDownload => !disposed && !Busy && LayoutRoot != null && available != null && Stage == null;
    internal bool CanInstall => !disposed && !Busy && LayoutRoot != null && Stage != null;
    internal StagedUpdate? Stage { get; private set; }
    internal DownloadProgress? Progress { get; private set; }
    internal string Message { get; private set; } = "Updates are checked only when you click Check for updates.";
    internal string Notes => available?.Release.Notes ?? "";
    internal DateTimeOffset? RetryAfter { get; private set; }
    internal event Action? Changed;

    internal ManualUpdateSession(ReleaseVersion current, string? layoutRoot,
        Func<CancellationToken, Task<ReleaseCheck>> check,
        Func<ReleaseInfo, CancellationToken, Task<ManifestVerification>> verify,
        Func<ReleaseInfo, IProgress<DownloadProgress>, CancellationToken, Task<DownloadResult>> download,
        IDisposable? resource = null)
    { Current = current; LayoutRoot = layoutRoot; this.check = check; this.verify = verify; this.download = download; this.resource = resource; }

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
        var http = GitHubReleaseClient.CreateHttpClient(); var client = new GitHubReleaseClient(http);
        return new(current, root, ct => client.CheckAsync(current, cancellationToken: ct),
            (release, ct) => client.VerifyManifestAsync(release, cancellationToken: ct),
            (release, progress, ct) => new UpdateDownloader(client).DownloadAsync(release, current, ReleasePolicy.ZipName(release.Version.Text), progress: progress, token: ct), http);
    }

    internal async Task CheckAsync()
    {
        if (disposed || Busy) return;
        if (RetryAfter is { } retry && DateTimeOffset.UtcNow < retry) { SetMessage($"GitHub asked us to wait. Try again after {retry.ToLocalTime():g}."); return; }
        using var cancel = new CancellationTokenSource(); operation = cancel;
        available = null; Stage = null; Progress = null; SetMessage("Checking GitHub for a newer release…");
        try
        {
            var result = await check(cancel.Token);
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
        catch (OperationCanceledException) { SetMessage("Update check cancelled."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { Diagnostics.Log("Update check", ex); SetMessage("Could not check for updates. Nothing was changed."); }
        finally { operation = null; Changed?.Invoke(); }
    }

    internal async Task DownloadAsync()
    {
        if (!CanDownload || disposed) return;
        using var cancel = new CancellationTokenSource(); operation = cancel;
        Progress = new(0, 0);
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
    public void Dispose() { disposed = true; Cancel(); resource?.Dispose(); }
}
