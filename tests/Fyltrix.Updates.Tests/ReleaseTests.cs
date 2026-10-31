using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fyltrix.Updates;

namespace Fyltrix.Updates.Tests;

public class ReleaseTests
{
    [Theory]
    [InlineData("01.2.3")][InlineData("1.2")][InlineData("v1.2.3")][InlineData("1.2.3+build")]
    [InlineData("1.2.3-01")][InlineData("65535.0.0")][InlineData("1.2.3\n")][InlineData("../1.2.3")]
    public void Invalid_layout_versions_are_rejected(string version) => Assert.Null(ReleaseVersion.Parse(version));

    [Fact]
    public void Semver_prereleases_compare_without_numeric_overflow()
    {
        string[] versions = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.99999999999999999999999999999",
            "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "2.0.0"];
        for (int i = 1; i < versions.Length; i++)
        {
            Assert.True(ReleaseVersion.Parse(versions[i])!.CompareTo(ReleaseVersion.Parse(versions[i - 1])) > 0);
            Assert.True(ReleaseVersion.Parse(versions[i - 1])!.CompareTo(ReleaseVersion.Parse(versions[i])) < 0);
        }
        Assert.Equal(0, ReleaseVersion.Parse("1.0.0")!.CompareTo(ReleaseVersion.Parse("1.0.0")));
    }
    [Fact]
    public void Valid_metadata_keeps_notes_as_untrusted_text()
    {
        using var fixture = new Fixture();
        var release = fixture.Release();
        Assert.Equal(Fixture.Version, release.Version.Text);
        Assert.Equal("<script>never run</script>", release.Notes);
        Assert.Equal(3, release.Assets.Count);
    }

    [Theory]
    [InlineData("draft")][InlineData("prerelease")][InlineData("tag")][InlineData("page")]
    [InlineData("asset-url")][InlineData("case-duplicate")][InlineData("asset-size")][InlineData("size-type")]
    [InlineData("state")][InlineData("name")][InlineData("missing-draft")][InlineData("root-duplicate")]
    [InlineData("trailing-json")][InlineData("oversized")][InlineData("too-many-assets")]
    public void Untrusted_metadata_is_rejected(string corruption)
    {
        using var fixture = new Fixture();
        var root = fixture.Metadata(); var asset = root["assets"]![0]!;
        switch (corruption)
        {
            case "draft": root["draft"] = true; break;
            case "prerelease": root["prerelease"] = true; break;
            case "tag": root["tag_name"] = "v0.2.0-beta"; break;
            case "page": root["html_url"] = "https://github.com/attacker/Fyltrix/releases/tag/v0.2.0"; break;
            case "asset-url": asset["browser_download_url"] = "https://github.com/imamulkadir/Fyltrix/releases/download/v0.2.0/x.exe?evil"; break;
            case "case-duplicate": var copy = asset.DeepClone(); copy["name"] = ReleasePolicy.SetupName.ToLowerInvariant(); ((JsonArray)root["assets"]!).Add(copy); break;
            case "asset-size": asset["size"] = ReleasePolicy.MaxPackageBytes + 1; break;
            case "size-type": asset["size"] = "256"; break;
            case "state": asset["state"] = "new"; break;
            case "name": asset["name"] = "../run.exe"; break;
            case "missing-draft": root.Remove("draft"); break;
            case "too-many-assets": for (int i = 0; i < 51; i++) ((JsonArray)root["assets"]!).Add(asset.DeepClone()); break;
        }
        byte[] bytes = Fixture.Bytes(root);
        if (corruption == "root-duplicate") bytes = Encoding.UTF8.GetBytes(root.ToJsonString().Replace("\"draft\":false", "\"draft\":true,\"draft\":false"));
        if (corruption == "trailing-json") bytes = Encoding.UTF8.GetBytes(root.ToJsonString() + "{}");
        if (corruption == "oversized") bytes = new byte[ReleasePolicy.MaxMetadataBytes + 1];
        Assert.Null(ReleaseInfo.Parse(bytes));
    }

    [Fact]
    public void Default_trust_is_empty_and_rotation_accepts_a_later_key()
    {
        using var fixture = new Fixture();
        byte[] data = fixture.Manifest, sig = fixture.Signature(data);
        Assert.Empty(ReleasePolicy.PinnedPublicKeys);
        Assert.Equal(VerificationFailure.NoTrustedKey, SignedManifest.Verify(fixture.Release(data, sig), data, sig).Failure);
        Assert.NotNull(SignedManifest.Verify(fixture.Release(data, sig), data, sig, ["invalid", fixture.PublicKey]).Verified);
    }

    [Theory]
    [InlineData("wrong-key")][InlineData("bad-base64")][InlineData("der")][InlineData("p384")][InlineData("changed-byte")]
    public void Invalid_signatures_never_produce_a_verified_release(string corruption)
    {
        using var fixture = new Fixture();
        byte[] data = fixture.Manifest, sig = fixture.Signature(data);
        string key = fixture.PublicKey;
        using var other = ECDsa.Create(corruption == "p384" ? ECCurve.NamedCurves.nistP384 : ECCurve.NamedCurves.nistP256);
        if (corruption is "wrong-key" or "p384") key = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
        if (corruption == "p384") sig = Encoding.ASCII.GetBytes(Convert.ToBase64String(other.SignData(data, HashAlgorithmName.SHA256)));
        if (corruption == "bad-base64") sig = Encoding.ASCII.GetBytes("!not-a-signature!");
        if (corruption == "der") sig = Encoding.ASCII.GetBytes(Convert.ToBase64String(fixture.Key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
        if (corruption == "changed-byte") data[0] ^= 1;
        Assert.Equal(VerificationFailure.InvalidSignature, SignedManifest.Verify(fixture.Release(data, sig), data, sig, [key]).Failure);
    }

    [Theory]
    [InlineData("product", VerificationFailure.InvalidManifest)]
    [InlineData("schema", VerificationFailure.InvalidManifest)]
    [InlineData("version", VerificationFailure.VersionMismatch)]
    [InlineData("size", VerificationFailure.AssetMismatch)]
    [InlineData("uppercase-hash", VerificationFailure.InvalidManifest)]
    [InlineData("sandbox", VerificationFailure.InvalidManifest)]
    [InlineData("unknown-field", VerificationFailure.InvalidManifest)]
    [InlineData("duplicate", VerificationFailure.InvalidManifest)]
    [InlineData("trailing-json", VerificationFailure.InvalidManifest)]
    public void Even_correctly_signed_manifests_must_obey_the_schema(string corruption, VerificationFailure expected)
    {
        using var fixture = new Fixture();
        var node = JsonNode.Parse(fixture.Manifest)!;
        switch (corruption)
        {
            case "product": node["product"] = "DynamicIsland"; break;
            case "schema": node["schemaVersion"] = 2; break;
            case "version": node["version"] = "0.1.0"; break;
            case "size": node["files"]![0]!["size"] = 123; break;
            case "uppercase-hash": node["files"]![0]!["sha256"] = new string('A', 64); break;
            case "sandbox": node["files"]![0]!["name"] = "Fyltrix-Sandbox-Setup-win-x64.exe"; break;
            case "unknown-field": node["publicKey"] = fixture.PublicKey; break;
        }
        string json = node.ToJsonString();
        if (corruption == "duplicate") json = json.Replace("\"product\":\"Fyltrix\"", "\"product\":\"Other\",\"product\":\"Fyltrix\"");
        if (corruption == "trailing-json") json += "{}";
        byte[] data = Encoding.UTF8.GetBytes(json), sig = fixture.Signature(data);
        Assert.Equal(expected, SignedManifest.Verify(fixture.Release(data, sig), data, sig, [fixture.PublicKey]).Failure);
    }
}
