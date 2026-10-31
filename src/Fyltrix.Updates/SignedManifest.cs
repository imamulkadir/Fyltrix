using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fyltrix.Updates;

public enum VerificationFailure
{
    None, NoTrustedKey, MissingManifest, InvalidSignature, InvalidManifest, VersionMismatch,
    AssetMismatch, PackageMismatch, InvalidPackage, Network, Timeout
}
public sealed record ManifestFile(string Name, long Size, string Sha256);

/// <summary>A verified manifest bound to one parsed release. No executable/install authority is implied.</summary>
public sealed class VerifiedRelease
{
    public ReleaseInfo Release { get; }
    public IReadOnlyList<ManifestFile> Files { get; }
    internal VerifiedRelease(ReleaseInfo release, List<ManifestFile> files) { Release = release; Files = files.AsReadOnly(); }
}
public sealed record ManifestVerification(VerificationFailure Failure, VerifiedRelease? Verified = null);

public static class SignedManifest
{
    public static bool IsP256(ECDsa key) => key.KeySize == 256
        && key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;

    public static ManifestVerification Verify(ReleaseInfo release, ReadOnlyMemory<byte> manifest,
        ReadOnlySpan<byte> signature, IReadOnlyList<string>? trustedKeys = null)
    {
        trustedKeys ??= ReleasePolicy.PinnedPublicKeys;
        if (trustedKeys.Count == 0) return new(VerificationFailure.NoTrustedKey);
        var manifestAsset = release.Assets.FirstOrDefault(a => a.Name == ReleasePolicy.ManifestName);
        var signatureAsset = release.Assets.FirstOrDefault(a => a.Name == ReleasePolicy.SignatureName);
        if (manifestAsset == null || signatureAsset == null) return new(VerificationFailure.MissingManifest);
        if (manifestAsset.Size != manifest.Length || signatureAsset.Size != signature.Length) return new(VerificationFailure.AssetMismatch);
        if (manifest.Length is 0 or > ReleasePolicy.MaxManifestBytes || signature.Length is 0 or > ReleasePolicy.MaxSignatureBytes)
            return new(VerificationFailure.InvalidSignature);
        byte[] signed;
        try { signed = Convert.FromBase64String(Encoding.ASCII.GetString(signature)); }
        catch (FormatException) { return new(VerificationFailure.InvalidSignature); }
        bool valid = false;
        if (signed.Length == 64)
            foreach (var encoded in trustedKeys)
            {
                try
                {
                    using var key = ECDsa.Create();
                    var bytes = Convert.FromBase64String(encoded);
                    key.ImportSubjectPublicKeyInfo(bytes, out int read);
                    if (read == bytes.Length && IsP256(key) && key.VerifyData(manifest.Span, signed, HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) { valid = true; break; }
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { }
            }
        if (!valid) return new(VerificationFailure.InvalidSignature);
        try
        {
            using var doc = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (!JsonRules.Unique(root) || !JsonRules.Exact(root, "schemaVersion", "product", "version", "files")
                || !root.GetProperty("schemaVersion").TryGetInt32(out int schema) || schema != 1
                || JsonRules.Text(root, "product") != "Fyltrix") return new(VerificationFailure.InvalidManifest);
            if (JsonRules.Text(root, "version") != release.Version.Text) return new(VerificationFailure.VersionMismatch);
            var array = root.GetProperty("files");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 2) return new(VerificationFailure.InvalidManifest);
            var files = new List<ManifestFile>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in array.EnumerateArray())
            {
                string? name = JsonRules.Text(item, "name"), hash = JsonRules.Text(item, "sha256");
                if (!JsonRules.Exact(item, "name", "size", "sha256") || name == null || !names.Add(name)
                    || !ReleasePolicy.IsPackage(name, release.Version.Text) || hash is not { Length: 64 }
                    || hash.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
                    || !item.GetProperty("size").TryGetInt64(out long size) || size is <= 0 or > ReleasePolicy.MaxPackageBytes)
                    return new(VerificationFailure.InvalidManifest);
                var asset = release.Assets.FirstOrDefault(a => a.Name == name);
                if (asset == null || asset.Size != size) return new(VerificationFailure.AssetMismatch);
                files.Add(new(name, size, hash));
            }
            return new(VerificationFailure.None, new(release, files));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return new(VerificationFailure.InvalidManifest); }
    }
}
