using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fyltrix.Updates;

namespace Fyltrix.Updates.Tests;

internal sealed class Fixture : IDisposable
{
    internal const string Version = "0.2.0";
    internal ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    internal byte[] Package { get; set; } = Pe();
    internal string Name { get; set; } = ReleasePolicy.SetupName;
    internal byte[] Manifest => ManifestFor(Name, Package);
    internal byte[] Signature(byte[] data) => Encoding.ASCII.GetBytes(Convert.ToBase64String(Key.SignData(data,
        HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    internal static byte[] ManifestFor(string name, byte[] package) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, product = "Fyltrix", version = Version,
        files = new[] { new { name, size = package.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant() } }
    });
    internal JsonObject Metadata(byte[]? manifest = null, byte[]? signature = null)
    {
        manifest ??= Manifest;
        signature ??= Signature(manifest);
        var assets = new JsonArray();
        foreach (var (name, length) in new[] { (Name, Package.LongLength), (ReleasePolicy.ManifestName, (long)manifest.Length), (ReleasePolicy.SignatureName, (long)signature.Length) })
            assets.Add(new JsonObject { ["name"] = name, ["size"] = length, ["state"] = "uploaded",
                ["browser_download_url"] = $"https://github.com/{ReleasePolicy.Repository}/releases/download/v{Version}/{name}" });
        return new JsonObject { ["tag_name"] = "v" + Version, ["html_url"] = $"https://github.com/{ReleasePolicy.Repository}/releases/tag/v{Version}",
            ["draft"] = false, ["prerelease"] = false, ["body"] = "<script>never run</script>", ["assets"] = assets };
    }
    internal ReleaseInfo Release(byte[]? manifest = null, byte[]? signature = null) => ReleaseInfo.Parse(Bytes(Metadata(manifest, signature)))!;
    internal VerifiedRelease Verified()
    {
        byte[] data = Manifest, sig = Signature(data);
        return SignedManifest.Verify(Release(data, sig), data, sig, [PublicKey]).Verified!;
    }
    internal static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
    internal static byte[] Pe()
    {
        var data = new byte[256]; data[0] = (byte)'M'; data[1] = (byte)'Z'; data[0x3c] = 0x80;
        data[0x80] = (byte)'P'; data[0x81] = (byte)'E'; return data;
    }
    public void Dispose() => Key.Dispose();
}

internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Requests.Add(request.RequestUri!);
        return send(request, token);
    }
}
