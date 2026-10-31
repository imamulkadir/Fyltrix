using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyltrix.Launcher;

public sealed record LayoutPointer(string Current, string? Previous);
public sealed record Payload(string Executable, string Version, bool UsedPrevious);

/// <summary>Read-only selection of a versioned payload. Never modifies pointers, application data, or binaries.</summary>
public static partial class ReleaseLayout
{
    public const int MaxPointerBytes = 4096;

    // A bounded, path-safe SemVer core with optional prerelease; build metadata is not used in layout folder names.
    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(-([0-9A-Za-z-]+\.)*[0-9A-Za-z-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool IsVersion(string? version)
    {
        if (version is not { Length: > 0 and <= 64 } || !VersionPattern().IsMatch(version)) return false;
        var separator = version.IndexOf('-');
        if (separator < 0) return true;
        return version[(separator + 1)..].Split('.').All(identifier =>
            !(identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit)));
    }

    public static LayoutPointer? ParsePointer(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxPointerBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? current = null, previous = null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name)) return null;
                switch (property.Name)
                {
                    case "current" when property.Value.ValueKind == JsonValueKind.String:
                        current = property.Value.GetString(); break;
                    case "previous" when property.Value.ValueKind == JsonValueKind.Null:
                        break;
                    case "previous" when property.Value.ValueKind == JsonValueKind.String:
                        previous = property.Value.GetString();
                        if (!IsVersion(previous)) return null;
                        break;
                    default: return null;
                }
            }
            return IsVersion(current) ? new LayoutPointer(current!, previous) : null;
        }
        catch (JsonException) { return null; }
    }

    public static Payload? Resolve(string root)
    {
        try
        {
            root = Path.GetFullPath(root);
            var pointerFile = Path.Combine(root, "current.json");
            if (IsLink(pointerFile)) return null;
            // Bound the read itself, including when another process replaces or extends the file.
            using var stream = new FileStream(pointerFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var bytes = new byte[MaxPointerBytes + 1];
            int length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            var pointer = ParsePointer(bytes.AsMemory(0, length));
            if (pointer == null) return null;
            return Find(root, pointer.Current, false)
                ?? (pointer.Previous == null ? null : Find(root, pointer.Previous, true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static Payload? Find(string root, string version, bool previous)
    {
        var folder = Path.Combine(root, "app-" + version);
        var executable = Path.Combine(folder, "Fyltrix.exe");
        // Reject junctions/symlinks at both payload boundaries. Never select the root launcher recursively.
        return Directory.Exists(folder) && !IsLink(folder) && File.Exists(executable) && !IsLink(executable)
            ? new Payload(executable, version, previous) : null;
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public static ProcessStartInfo StartInfo(Payload payload, IEnumerable<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(payload.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
}
