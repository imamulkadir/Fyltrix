namespace Fyltrix.Updates;

public enum DownloadFailure { None, NotNewer, Untrusted, InvalidAsset, InvalidDestination, NoSpace, InvalidResponse, PackageRejected, Network, TimedOut, CouldNotSave }
public sealed record DownloadResult(DownloadFailure Failure, StagedUpdate? Staged = null, VerificationFailure Verification = VerificationFailure.None);
public sealed record DownloadProgress(long Received, long Total);
public sealed class StagedUpdate
{
    public string Directory { get; }
    public string PackagePath { get; }
    public VerifiedRelease Verified { get; }
    public string AssetName { get; }
    internal string Token { get; }
    internal StagedUpdate(string folder, string name, VerifiedRelease verified, string token)
    { Directory = folder; AssetName = name; PackagePath = Path.Combine(folder, name); Verified = verified; Token = token; }
}

/// <summary>Explicit, verified downloads only. No pruning, execution, profile changes, or installation.</summary>
public sealed class UpdateDownloader
{
    private readonly GitHubReleaseClient client;
    private readonly TimeSpan stall;
    private readonly Func<string, long> freeBytes;
    public UpdateDownloader(GitHubReleaseClient client, TimeSpan? stallTimeout = null, Func<string, long>? availableBytes = null)
    {
        this.client = client; stall = stallTimeout ?? TimeSpan.FromSeconds(30);
        if (stall <= TimeSpan.Zero || stall > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(stallTimeout));
        freeBytes = availableBytes ?? (path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace);
    }
    public async Task<DownloadResult> DownloadAsync(ReleaseInfo release, ReleaseVersion current, string assetName,
        string? stagingRoot = null, IProgress<DownloadProgress>? progress = null, CancellationToken token = default)
        => await DownloadWithKeysAsync(release, current, assetName, stagingRoot, progress, token, ReleasePolicy.PinnedPublicKeys).ConfigureAwait(false);

    internal async Task<DownloadResult> DownloadWithKeysAsync(ReleaseInfo release, ReleaseVersion current, string assetName,
        string? stagingRoot, IProgress<DownloadProgress>? progress, CancellationToken token, IReadOnlyList<string> testKeys)
    {
        if (release.Version.CompareTo(current) <= 0) return new(DownloadFailure.NotNewer);
        var verified = await client.VerifyManifestAsync(release, testKeys, token).ConfigureAwait(false);
        if (verified.Verified == null) return new(DownloadFailure.Untrusted, Verification: verified.Failure);
        return await StageAsync(verified.Verified, assetName, stagingRoot, async (asset, ct) =>
        {
            var response = await client.OpenDownloadAsync(asset.Url, ct).ConfigureAwait(false);
            if (response == null) throw new InvalidDataException("Invalid download response.");
            try
            {
                if (response.Content.Headers.ContentLength is { } length && length != asset.Size) throw new InvalidDataException("Wrong download length.");
                var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return new Input(source, response);
            }
            catch { response.Dispose(); throw; }
        }, progress, token).ConfigureAwait(false);
    }

    /// <summary>Explicit offline owner/test trust. Never configures production keys or contacts a URL.</summary>
    public async Task<DownloadResult> StageLocalAsync(ReleaseInfo release, ReleaseVersion current, ReadOnlyMemory<byte> manifest,
        ReadOnlyMemory<byte> signature, string packagePath, string publicKey, string stagingRoot,
        IProgress<DownloadProgress>? progress = null, CancellationToken token = default)
    {
        if (release.Version.CompareTo(current) <= 0) return new(DownloadFailure.NotNewer);
        var verified = SignedManifest.Verify(release, manifest, signature.Span, [publicKey]);
        if (verified.Verified == null) return new(DownloadFailure.Untrusted, Verification: verified.Failure);
        return await StageAsync(verified.Verified, Path.GetFileName(packagePath), stagingRoot,
            (_, _) => Task.FromResult(new Input(File.OpenRead(packagePath))), progress, token).ConfigureAwait(false);
    }

    private async Task<DownloadResult> StageAsync(VerifiedRelease verified, string name, string? root,
        Func<ReleaseAsset, CancellationToken, Task<Input>> open, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var file = verified.Files.FirstOrDefault(f => f.Name == name);
        var asset = verified.Release.Assets.FirstOrDefault(a => a.Name == name);
        if (file == null || asset == null) return new(DownloadFailure.InvalidAsset);
        try { root = StagingArea.ValidateRoot(root ?? StagingArea.DefaultRoot); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return new(DownloadFailure.InvalidDestination); }
        string id = Guid.NewGuid().ToString("N"), folder = Path.Combine(root, "stage-" + id);
        bool owned = false, success = false;
        using var clock = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (freeBytes(root) < file.Size + 200L * 1024 * 1024) return new(DownloadFailure.NoSpace);
            Directory.CreateDirectory(folder);
            StagingArea.RejectLinks(folder);
            await StagingArea.WriteNewAsync(Path.Combine(folder, StagingArea.Marker), System.Text.Encoding.ASCII.GetBytes(id), token).ConfigureAwait(false);
            owned = true;
            clock.CancelAfter(stall);
            using var source = await open(asset, clock.Token).ConfigureAwait(false);
            string partial = Path.Combine(folder, name + ".partial");
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, true))
            {
                byte[] bytes = new byte[64 * 1024]; long total = 0, reported = 0;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    clock.CancelAfter(stall);
                    int read = await source.Stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, file.Size + 1 - total)), clock.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > file.Size) return new(DownloadFailure.InvalidResponse);
                    await output.WriteAsync(bytes.AsMemory(0, read), clock.Token).ConfigureAwait(false);
                    if (watch.ElapsedMilliseconds >= 100)
                    { progress?.Report(new(total, file.Size)); reported = total; watch.Restart(); }
                }
                clock.CancelAfter(Timeout.InfiniteTimeSpan);
                if (total != file.Size) return new(DownloadFailure.InvalidResponse);
                await output.FlushAsync(token).ConfigureAwait(false);
                var check = await PackageVerifier.VerifyStreamAsync(verified, name, output, token).ConfigureAwait(false);
                if (check != VerificationFailure.None) return new(DownloadFailure.PackageRejected, Verification: check);
                if (reported != total) progress?.Report(new(total, file.Size));
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, Path.Combine(folder, name));
            await StagingArea.WriteNewAsync(Path.Combine(folder, "release.json"), verified.Release.SerializeEvidence(), token).ConfigureAwait(false);
            await StagingArea.WriteNewAsync(Path.Combine(folder, ReleasePolicy.ManifestName), verified.ManifestBytes, token).ConfigureAwait(false);
            await StagingArea.WriteNewAsync(Path.Combine(folder, ReleasePolicy.SignatureName), verified.SignatureBytes, token).ConfigureAwait(false);
            success = true;
            return new(DownloadFailure.None, new(folder, name, verified, id));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(DownloadFailure.TimedOut); }
        catch (HttpRequestException) { return new(DownloadFailure.Network); }
        catch (InvalidDataException) { return new(DownloadFailure.InvalidResponse); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(DownloadFailure.CouldNotSave); }
        finally { if (owned && !success) StagingArea.CleanFailed(folder, id, name); }
    }
    private sealed class Input(Stream stream, IDisposable? owner = null) : IDisposable
    {
        public Stream Stream { get; } = stream;
        public void Dispose() { Stream.Dispose(); owner?.Dispose(); }
    }
}
