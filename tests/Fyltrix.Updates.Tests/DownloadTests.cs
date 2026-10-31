using System.Net;
using Fyltrix.Updates;

namespace Fyltrix.Updates.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Fyltrix-DownloadTests-" + Guid.NewGuid().ToString("N"));
    public DownloadTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private static ReleaseVersion Current => ReleaseVersion.Parse("0.1.0")!;
    [Theory]
    [InlineData("valid", DownloadFailure.None)] [InlineData("short", DownloadFailure.InvalidResponse)]
    [InlineData("long", DownloadFailure.InvalidResponse)] [InlineData("hash", DownloadFailure.PackageRejected)]
    [InlineData("space", DownloadFailure.NoSpace)] [InlineData("length-header", DownloadFailure.InvalidResponse)]
    [InlineData("network", DownloadFailure.Network)] [InlineData("stall", DownloadFailure.TimedOut)]
    public async Task Downloads_are_bounded_verified_and_cleaned_on_failure(string variant, DownloadFailure expected)
    {
        using var fixture = new Fixture();
        byte[] manifest = fixture.Manifest, sig = fixture.Signature(manifest), package = fixture.Package.ToArray();
        if (variant == "short") package = package[..^1];
        if (variant == "long") package = [.. package, 0];
        if (variant == "hash") package[^1] ^= 1;
        using var handler = new Handler((request, _) =>
        {
            string name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            if (name == fixture.Name && variant == "network") throw new HttpRequestException("test interruption");
            HttpContent body = name == ReleasePolicy.ManifestName ? new ByteArrayContent(manifest)
                : name == ReleasePolicy.SignatureName ? new ByteArrayContent(sig)
                : variant == "stall" ? new StreamContent(new StalledStream()) : new StreamContent(new Unseekable(package));
            if (name == fixture.Name && variant == "length-header") body.Headers.ContentLength = 99;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        });
        using var http = new HttpClient(handler);
        string staging = Path.Combine(root, "staging & files");
        var progress = new Reports();
        var downloader = new UpdateDownloader(new GitHubReleaseClient(http), TimeSpan.FromMilliseconds(150),
            _ => variant == "space" ? 0 : long.MaxValue);
        var result = await downloader.DownloadWithKeysAsync(fixture.Release(manifest, sig), Current, fixture.Name,
            staging, progress, CancellationToken.None, [fixture.PublicKey]);
        Assert.Equal(expected, result.Failure);
        if (expected == DownloadFailure.None)
        {
            Assert.NotNull(result.Staged);
            Assert.Equal(fixture.Package, await File.ReadAllBytesAsync(result.Staged.PackagePath));
            Assert.False(File.Exists(result.Staged.PackagePath + ".partial"));
            Assert.Equal(fixture.Package.Length, progress.Values.Last().Received);
            Assert.NotNull(await UpdateHandover.LoadStageAsync(result.Staged.Directory, [fixture.PublicKey]));
            Assert.Null(await UpdateHandover.LoadStageAsync(result.Staged.Directory)); // Test trust is not production trust.
        }
        else
        {
            Assert.Null(result.Staged);
            Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
        }
        if (variant == "space") Assert.DoesNotContain(handler.Requests, u => u.AbsolutePath.EndsWith(fixture.Name));
    }
    [Fact]
    public async Task Unconfigured_trust_and_old_versions_never_request_package_or_create_staging()
    {
        using var fixture = new Fixture();
        using var handler = new Handler((_, _) => throw new Exception("No network expected"));
        using var http = new HttpClient(handler);
        var downloader = new UpdateDownloader(new GitHubReleaseClient(http));
        Assert.Equal(DownloadFailure.Untrusted, (await downloader.DownloadWithKeysAsync(fixture.Release(), Current, fixture.Name, root, null, default, Array.Empty<string>())).Failure);
        Assert.Equal(DownloadFailure.NotNewer, (await downloader.DownloadAsync(fixture.Release(), ReleaseVersion.Parse("0.3.0")!, fixture.Name, root)).Failure);
        Assert.Empty(handler.Requests); Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }
    [Fact]
    public async Task Cancellation_discards_only_owned_partial_files()
    {
        using var fixture = new Fixture();
        byte[] manifest = fixture.Manifest, sig = fixture.Signature(manifest);
        using var cancel = new CancellationTokenSource();
        using var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = request.RequestUri!.AbsolutePath.EndsWith(fixture.Name) ? new StreamContent(new CancelOnRead(cancel))
            : new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith(ReleasePolicy.ManifestName) ? manifest : sig) }));
        using var http = new HttpClient(handler);
        string sentinel = Path.Combine(root, "keep.txt"); await File.WriteAllTextAsync(sentinel, "untouched");
        var download = new UpdateDownloader(new GitHubReleaseClient(http));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.DownloadWithKeysAsync(fixture.Release(manifest, sig), Current,
            fixture.Name, root, null, cancel.Token, [fixture.PublicKey]));
        Assert.Equal("untouched", await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(root));
    }
    [Theory]
    [InlineData("checkout")] [InlineData("roaming")] [InlineData("local")] [InlineData("relative")] [InlineData("application")]
    public async Task Protected_destinations_are_refused_before_copying(string kind)
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("Offline test must not use HTTP")));
        string input = Path.Combine(root, fixture.Name); await File.WriteAllBytesAsync(input, fixture.Package);
        string target = Path.Combine(root, "protected");
        Directory.CreateDirectory(target);
        if (kind == "checkout") Directory.CreateDirectory(Path.Combine(target, ".git"));
        if (kind == "application") await File.WriteAllTextAsync(Path.Combine(target, "Fyltrix.exe"), "sentinel");
        target = kind switch
        {
            "roaming" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileLens", "MustNotCreate"),
            "local" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileLens", "MustNotCreate"),
            "relative" => "relative-staging", _ => Path.Combine(target, "staging")
        };
        byte[] manifest = fixture.Manifest, sig = fixture.Signature(manifest);
        var result = await new UpdateDownloader(new GitHubReleaseClient(http)).StageLocalAsync(fixture.Release(manifest, sig), Current,
            manifest, sig, input, fixture.PublicKey, target);
        Assert.Equal(DownloadFailure.InvalidDestination, result.Failure);
        Assert.False(Directory.Exists(target));
    }
    internal sealed class Reports : IProgress<DownloadProgress>
    {
        internal List<DownloadProgress> Values { get; } = [];
        public void Report(DownloadProgress value) => Values.Add(value);
    }
    [Fact]
    public void Staging_rejects_a_real_junction_without_writing_through_it()
    {
        string target = Path.Combine(root, "target"), link = Path.Combine(root, "junction");
        Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "keep.txt"), "untouched");
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        string script = "New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Value '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null";
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        using var command = System.Diagnostics.Process.Start(start)!; command.WaitForExit();
        Assert.Equal(0, command.ExitCode);
        try
        {
            Assert.Throws<IOException>(() => StagingArea.ValidateRoot(Path.Combine(link, "staging")));
            Assert.Single(Directory.GetFileSystemEntries(target));
        }
        finally { Directory.Delete(link); } // Delete only the junction itself before removing our test root.
    }
    private sealed class Unseekable(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
    private sealed class StalledStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token); return 0; }
    }
    private sealed class CancelOnRead(CancellationTokenSource cancel) : MemoryStream
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { cancel.Cancel(); return ValueTask.FromCanceled<int>(token); }
    }
}
