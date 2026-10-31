using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyltrix.Updates;

public static partial class ReleasePolicy
{
    public const string Repository = "imamulkadir/Fyltrix";
    public const string ApiUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string ManifestName = "Fyltrix-release.json";
    public const string SignatureName = ManifestName + ".sig";
    public const string SetupName = "Fyltrix-Setup-win-x64.exe";
    public const int MaxMetadataBytes = 256 * 1024;
    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxSignatureBytes = 1024;
    public const long MaxPackageBytes = 1024L * 1024 * 1024;
    // Intentionally empty. Never take trust keys from a release/feed, and never reuse DynamicEye's key.
    public static IReadOnlyList<string> PinnedPublicKeys { get; } = Array.AsReadOnly(Array.Empty<string>());
    public static string ZipName(string version) => $"Fyltrix-{version}-win-x64.zip";
    public static bool IsPackage(string name, string version) => name == SetupName || name == ZipName(version);
    public static bool SafeName(string name) => name.Length <= 128 && NamePattern().IsMatch(name)
        && !name.EndsWith('.') && !name.Contains("..");
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

public sealed record ReleaseAsset(string Name, long Size, Uri Url);

public sealed class ReleaseInfo
{
    public ReleaseVersion Version { get; }
    public string Tag { get; }
    public Uri Page { get; }
    /// <summary>Untrusted plain text. Never render as HTML or execute release instructions.</summary>
    public string Notes { get; }
    public IReadOnlyList<ReleaseAsset> Assets { get; }
    private ReleaseInfo(ReleaseVersion version, string tag, Uri page, string notes, List<ReleaseAsset> assets)
    { Version = version; Tag = tag; Page = page; Notes = notes; Assets = assets.AsReadOnly(); }

    public static ReleaseInfo? Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length is 0 or > ReleasePolicy.MaxMetadataBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (!JsonRules.Unique(root) || root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
                || !root.TryGetProperty("prerelease", out var pre) || pre.ValueKind != JsonValueKind.False) return null;
            string? tag = JsonRules.Text(root, "tag_name");
            var version = ReleaseVersion.Parse(tag?.StartsWith('v') == true ? tag[1..] : tag);
            if (version == null || version.IsPrerelease) return null; // First implementation: stable channel only.
            string prefix = "https://github.com/" + ReleasePolicy.Repository + "/releases/";
            string page = prefix + "tag/" + tag;
            if (JsonRules.Text(root, "html_url") != page) return null;
            if (!root.TryGetProperty("assets", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 50) return null;
            var assets = new List<ReleaseAsset>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in list.EnumerateArray())
            {
                string? name = JsonRules.Text(item, "name");
                if (name == null || !ReleasePolicy.SafeName(name) || !names.Add(name)
                    || !item.TryGetProperty("size", out var size) || !size.TryGetInt64(out long bytes)
                    || bytes is <= 0 or > ReleasePolicy.MaxPackageBytes
                    || JsonRules.Text(item, "state") != "uploaded") return null;
                string url = prefix + "download/" + tag + "/" + name;
                if (JsonRules.Text(item, "browser_download_url") != url) return null;
                assets.Add(new(name, bytes, new Uri(url)));
            }
            var notes = JsonRules.Text(root, "body") ?? "";
            return new(version, tag!, new Uri(page), notes[..Math.Min(notes.Length, 16 * 1024)], assets);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }
}

internal static class JsonRules
{
    internal static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static bool Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            return element.EnumerateObject().All(p => names.Add(p.Name) && Unique(p.Value));
        }
        return element.ValueKind != JsonValueKind.Array || element.EnumerateArray().All(Unique);
    }
    internal static bool Exact(JsonElement element, params string[] names) => element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(names.Order());
}
