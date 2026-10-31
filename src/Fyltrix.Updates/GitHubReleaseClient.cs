using System.Net;
using System.Net.Http.Headers;

namespace Fyltrix.Updates;

public enum CheckStatus { Available, UpToDate, NoRelease, NotModified, RateLimited, InvalidMetadata, Network, Timeout }
public sealed record ReleaseCheck(CheckStatus Status, ReleaseInfo? Release = null, string? ETag = null, DateTimeOffset? RetryAfter = null);

/// <summary>Explicit GET requests only. No polling, profile persistence, package downloading, or installation.</summary>
public sealed class GitHubReleaseClient
{
    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    // Injected clients must disable automatic redirects. Production callers should use CreateHttpClient().
    public GitHubReleaseClient(HttpClient http, TimeSpan? timeout = null)
    {
        this.http = http;
        this.timeout = timeout ?? TimeSpan.FromSeconds(15);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public async Task<ReleaseCheck> CheckAsync(ReleaseVersion current, string? etag = null, CancellationToken cancellationToken = default)
    {
        using var clock = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        clock.CancelAfter(timeout);
        try
        {
            using var request = Request(new Uri(ReleasePolicy.ApiUrl), true);
            if (etag != null && EntityTagHeaderValue.TryParse(etag, out var value)) request.Headers.IfNoneMatch.Add(value);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, clock.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } final && final.AbsoluteUri != ReleasePolicy.ApiUrl) return new(CheckStatus.InvalidMetadata);
            if (response.StatusCode == HttpStatusCode.NotModified) return new(CheckStatus.NotModified, ETag: etag);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(CheckStatus.NoRelease);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var retry = RetryAt(response);
                return retry == null ? new(CheckStatus.Network) : new(CheckStatus.RateLimited, RetryAfter: retry);
            }
            if (response.StatusCode != HttpStatusCode.OK) return new(CheckStatus.Network);
            byte[]? bytes = await ReadBoundedAsync(response.Content, ReleasePolicy.MaxMetadataBytes, clock.Token).ConfigureAwait(false);
            var release = bytes == null ? null : ReleaseInfo.Parse(bytes);
            return release == null ? new(CheckStatus.InvalidMetadata) : new(
                release.Version.CompareTo(current) > 0 ? CheckStatus.Available : CheckStatus.UpToDate,
                release, response.Headers.ETag?.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(CheckStatus.Timeout); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return new(CheckStatus.Network); }
    }

    public async Task<ManifestVerification> VerifyManifestAsync(ReleaseInfo release, IReadOnlyList<string>? trustedKeys = null,
        CancellationToken cancellationToken = default)
    {
        trustedKeys ??= ReleasePolicy.PinnedPublicKeys;
        if (trustedKeys.Count == 0) return new(VerificationFailure.NoTrustedKey); // No network when no trust root exists.
        var manifest = release.Assets.FirstOrDefault(a => a.Name == ReleasePolicy.ManifestName);
        var signature = release.Assets.FirstOrDefault(a => a.Name == ReleasePolicy.SignatureName);
        if (manifest == null || signature == null) return new(VerificationFailure.MissingManifest);
        if (manifest.Size > ReleasePolicy.MaxManifestBytes || signature.Size > ReleasePolicy.MaxSignatureBytes) return new(VerificationFailure.InvalidManifest);
        using var clock = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        clock.CancelAfter(timeout);
        try
        {
            var data = await ReadAssetAsync(manifest, ReleasePolicy.MaxManifestBytes, clock.Token).ConfigureAwait(false);
            if (data == null) return new(VerificationFailure.InvalidManifest);
            var sig = await ReadAssetAsync(signature, ReleasePolicy.MaxSignatureBytes, clock.Token).ConfigureAwait(false);
            if (sig == null) return new(VerificationFailure.InvalidManifest);
            return SignedManifest.Verify(release, data, sig, trustedKeys);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(VerificationFailure.Timeout); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return new(VerificationFailure.Network); }
    }

    private async Task<byte[]?> ReadAssetAsync(ReleaseAsset asset, int limit, CancellationToken token)
    {
        using var response = await OpenDownloadAsync(asset.Url, token).ConfigureAwait(false);
        if (response == null) return null;
        var bytes = await ReadBoundedAsync(response.Content, limit, token).ConfigureAwait(false);
        return bytes?.LongLength == asset.Size ? bytes : null;
    }

    internal async Task<HttpResponseMessage?> OpenDownloadAsync(Uri url, CancellationToken token)
    {
        for (int redirects = 0; redirects <= 4; redirects++)
        {
            using var request = Request(url, false);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            bool retained = false;
            try
            {
                if (response.RequestMessage?.RequestUri is { } final && !AllowedDownload(final)) return null;
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (response.Headers.Location == null) return null;
                    try { url = new Uri(url, response.Headers.Location); }
                    catch (UriFormatException) { return null; }
                    if (!AllowedDownload(url)) return null;
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK) return null;
                retained = true;
                return response;
            }
            finally { if (!retained) response.Dispose(); }
        }
        return null;
    }
    private static bool AllowedDownload(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    private static HttpRequestMessage Request(Uri uri, bool api)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Fyltrix-UpdateVerifier/1.0 (+https://github.com/" + ReleasePolicy.Repository + ")");
        request.Headers.Accept.ParseAdd(api ? "application/vnd.github+json" : "application/octet-stream");
        if (api) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }
    private static DateTimeOffset? RetryAt(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Date is { } date) return date;
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Clamp(delta.TotalSeconds, 0, 86400));
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0"
            && response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out long seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { }
        }
        return response.StatusCode == HttpStatusCode.TooManyRequests ? DateTimeOffset.UtcNow.AddHours(1) : null;
    }
    public static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit) return null;
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await ReadBoundedAsync(stream, limit, token).ConfigureAwait(false);
    }
    public static async Task<byte[]?> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(16 * 1024, limit + 1)];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit + 1L - output.Length)), token).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            output.Write(buffer, 0, read);
            if (output.Length > limit) return null;
        }
    }
}
