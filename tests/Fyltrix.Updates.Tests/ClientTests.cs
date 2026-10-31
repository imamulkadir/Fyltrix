using System.Net;
using System.Net.Http.Headers;
using Fyltrix.Updates;

namespace Fyltrix.Updates.Tests;

public class ClientTests
{
    [Theory]
    [InlineData("0.1.0", CheckStatus.Available)]
    [InlineData("0.2.0-beta.10", CheckStatus.Available)]
    [InlineData("0.2.0", CheckStatus.UpToDate)]
    [InlineData("0.3.0", CheckStatus.UpToDate)]
    public async Task Check_uses_only_one_get_with_conditional_headers(string current, CheckStatus status)
    {
        using var fixture = new Fixture();
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(ReleasePolicy.ApiUrl, request.RequestUri!.AbsoluteUri);
            Assert.Contains("Fyltrix-UpdateVerifier", request.Headers.UserAgent.ToString());
            Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            Assert.Equal("\"previous\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            Assert.Null(request.Headers.Authorization);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Fixture.Bytes(fixture.Metadata())) };
            response.Headers.ETag = new EntityTagHeaderValue("\"next\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var result = await new GitHubReleaseClient(http).CheckAsync(ReleaseVersion.Parse(current)!, "\"previous\"");
        Assert.Equal(status, result.Status);
        Assert.Equal("\"next\"", result.ETag);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(304, CheckStatus.NotModified)] [InlineData(404, CheckStatus.NoRelease)]
    [InlineData(429, CheckStatus.RateLimited)] [InlineData(403, CheckStatus.Network)]
    [InlineData(500, CheckStatus.Network)] [InlineData(302, CheckStatus.Network)]
    public async Task Http_outcomes_are_not_misreported_as_updates(int code, CheckStatus expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)));
        using var http = new HttpClient(handler);
        var result = await new GitHubReleaseClient(http).CheckAsync(ReleaseVersion.Parse("0.1.0")!);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Release);
    }
    [Fact]
    public async Task Rate_limit_reset_and_retry_after_are_reported_without_retrying()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(5);
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var result = await new GitHubReleaseClient(http).CheckAsync(ReleaseVersion.Parse("0.1.0")!);
        Assert.Equal(CheckStatus.RateLimited, result.Status);
        Assert.Equal(reset.ToUnixTimeSeconds(), result.RetryAfter!.Value.ToUnixTimeSeconds());
        Assert.Single(handler.Requests);
    }
    [Fact]
    public async Task Response_body_is_bounded_even_without_content_length()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new Unseekable(new byte[ReleasePolicy.MaxMetadataBytes + 1])) }));
        using var http = new HttpClient(handler);
        Assert.Equal(CheckStatus.InvalidMetadata, (await new GitHubReleaseClient(http).CheckAsync(ReleaseVersion.Parse("0.1.0")!)).Status);
    }
    [Fact]
    public async Task Timeout_and_owner_cancellation_are_distinct()
    {
        using var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); });
        using var http = new HttpClient(handler);
        var client = new GitHubReleaseClient(http, TimeSpan.FromMilliseconds(50));
        Assert.Equal(CheckStatus.Timeout, (await client.CheckAsync(ReleaseVersion.Parse("0.1.0")!)).Status);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckAsync(ReleaseVersion.Parse("0.1.0")!, cancellationToken: cancel.Token));
    }
    [Fact]
    public async Task No_pinned_key_means_no_manifest_or_package_requests()
    {
        using var fixture = new Fixture();
        using var handler = new Handler((_, _) => throw new Exception("Network must not be used"));
        using var http = new HttpClient(handler);
        Assert.Equal(VerificationFailure.NoTrustedKey, (await new GitHubReleaseClient(http).VerifyManifestAsync(fixture.Release(), Array.Empty<string>())).Failure);
        Assert.Empty(handler.Requests);
    }
    [Fact]
    public async Task Missing_manifest_is_refused_before_network_access()
    {
        using var fixture = new Fixture();
        var node = fixture.Metadata(); ((System.Text.Json.Nodes.JsonArray)node["assets"]!).RemoveAt(1);
        using var handler = new Handler((_, _) => throw new Exception("Missing assets must not trigger network access"));
        using var http = new HttpClient(handler);
        var release = ReleaseInfo.Parse(Fixture.Bytes(node))!;
        Assert.Equal(VerificationFailure.MissingManifest, (await new GitHubReleaseClient(http).VerifyManifestAsync(release, [fixture.PublicKey])).Failure);
        Assert.Empty(handler.Requests);
    }
    [Fact]
    public async Task Stalled_response_body_is_covered_by_the_timeout()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new StalledStream()) }));
        using var http = new HttpClient(handler);
        Assert.Equal(CheckStatus.Timeout, (await new GitHubReleaseClient(http, TimeSpan.FromMilliseconds(50))
            .CheckAsync(ReleaseVersion.Parse("0.1.0")!)).Status);
    }
    [Fact]
    public async Task Retry_after_delta_is_returned_without_an_automatic_retry()
    {
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var before = DateTimeOffset.UtcNow;
        var result = await new GitHubReleaseClient(http).CheckAsync(ReleaseVersion.Parse("0.1.0")!);
        Assert.Equal(CheckStatus.RateLimited, result.Status);
        Assert.InRange(result.RetryAfter!.Value, before.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(11));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Manifest_transport_verifies_but_never_requests_the_package(bool tamper)
    {
        using var fixture = new Fixture();
        byte[] manifest = fixture.Manifest, signature = fixture.Signature(manifest);
        var release = fixture.Release(manifest, signature);
        if (tamper) manifest[0] ^= 1;
        using var handler = new Handler((request, _) =>
        {
            string name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            Assert.NotEqual(ReleasePolicy.SetupName, name);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(name == ReleasePolicy.ManifestName ? manifest : signature) });
        });
        using var http = new HttpClient(handler);
        var result = await new GitHubReleaseClient(http).VerifyManifestAsync(release, [fixture.PublicKey]);
        Assert.Equal(tamper ? VerificationFailure.InvalidSignature : VerificationFailure.None, result.Failure);
        Assert.Equal(2, handler.Requests.Count);
    }
    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/asset?token=test", true)]
    [InlineData("https://github.com.attacker.invalid/payload", false)]
    [InlineData("http://github.com/payload", false)]
    [InlineData("https://github.com:444/payload", false)]
    [InlineData("https://user@github.com/payload", false)]
    public async Task Redirects_are_bounded_to_https_download_hosts(string location, bool permitted)
    {
        using var fixture = new Fixture();
        byte[] manifest = fixture.Manifest, signature = fixture.Signature(manifest);
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(ReleasePolicy.ManifestName))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri(location); return Task.FromResult(redirect);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(
                request.RequestUri.Host == "release-assets.githubusercontent.com" ? manifest : signature) });
        });
        using var http = new HttpClient(handler);
        var result = await new GitHubReleaseClient(http).VerifyManifestAsync(fixture.Release(manifest, signature), [fixture.PublicKey]);
        Assert.Equal(permitted ? VerificationFailure.None : VerificationFailure.InvalidManifest, result.Failure);
        Assert.DoesNotContain(handler.Requests, u => u.Host.EndsWith("invalid") || u.Scheme == "http");
    }
    [Fact]
    public async Task Redirect_loops_cannot_poll_forever()
    {
        using var fixture = new Fixture();
        using var handler = new Handler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = request.RequestUri; return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        Assert.Equal(VerificationFailure.InvalidManifest, (await new GitHubReleaseClient(http).VerifyManifestAsync(fixture.Release(), [fixture.PublicKey])).Failure);
        Assert.InRange(handler.Requests.Count, 1, 5);
    }
    private sealed class Unseekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
        }
    }
}
