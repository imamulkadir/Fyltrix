using System.Text;
using System.Runtime.InteropServices;

namespace Fyltrix.Updates;

/// <summary>Separate updater data. Never cleans arbitrary version/profile folders.</summary>
public static class StagingArea
{
    internal const string Marker = ".fyltrix-update-stage";
    internal static readonly string[] EvidenceFiles = ["release.json", ReleasePolicy.ManifestName, ReleasePolicy.SignatureName];
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fyltrix", "Updates");
    internal static bool Within(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static string ValidateRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\")) throw new ArgumentException("Use an absolute local staging directory.");
        RejectLinks(root);
        root = CanonicalPath(root);
        if (root == Path.GetPathRoot(root)) throw new ArgumentException("Do not stage at a drive root.");
        foreach (var kind in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            string profile = CanonicalPath(Path.Combine(Environment.GetFolderPath(kind), "FileLens"));
            if (Within(root, profile) || Within(profile, root)) throw new ArgumentException("Staging must not overlap existing application data.");
        }
        RejectLinks(root);
        for (var parent = new DirectoryInfo(root); parent != null; parent = parent.Parent)
            if (Directory.Exists(Path.Combine(parent.FullName, ".git")) || File.Exists(Path.Combine(parent.FullName, ".git"))
                || File.Exists(Path.Combine(parent.FullName, "publish.ps1")) || File.Exists(Path.Combine(parent.FullName, "Fyltrix.exe"))
                || File.Exists(Path.Combine(parent.FullName, ".fyltrix-install.ini")))
                throw new ArgumentException("Staging must be outside application copies and development checkouts.");
        return root;
    }
    internal static void RejectLinks(string path)
    {
        for (var item = new FileInfo(Path.GetFullPath(path)); item != null; item = item.Directory == null ? null : new FileInfo(item.Directory.FullName))
            if ((File.Exists(item.FullName) || Directory.Exists(item.FullName)) && (File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Redirected staging paths are not allowed.");
    }
    // Expand the existing ancestor, including Windows 8.3 aliases, before comparing protected directories.
    internal static string CanonicalPath(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows()) return path;
        var suffix = new Stack<string>(); string existing = path;
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            suffix.Push(Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing) ?? throw new IOException("No existing path ancestor.");
        }
        var buffer = new StringBuilder(32768);
        int length = GetLongPathName(existing, buffer, buffer.Capacity);
        if (length <= 0 || length >= buffer.Capacity) throw new IOException("Could not resolve staging path.");
        existing = buffer.ToString();
        while (suffix.Count > 0) existing = Path.Combine(existing, suffix.Pop());
        return Path.TrimEndingDirectorySeparator(existing);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetLongPathName(string path, StringBuilder expanded, int length);
    internal static async Task WriteNewAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await file.WriteAsync(bytes, token).ConfigureAwait(false);
        await file.FlushAsync(token).ConfigureAwait(false);
    }
    internal static async Task<byte[]> ReadSmallAsync(string path, int limit, CancellationToken token)
    {
        RejectLinks(path);
        await using var file = File.OpenRead(path);
        return await GitHubReleaseClient.ReadBoundedAsync(file, limit, token).ConfigureAwait(false) ?? throw new InvalidDataException("Oversized staging evidence.");
    }
    internal static void CleanFailed(string folder, string token, string name)
    {
        try
        {
            RejectLinks(folder);
            using (var marker = File.OpenRead(Path.Combine(folder, Marker)))
            {
                var bytes = new byte[33];
                int read = marker.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                if (read != 32 || Encoding.ASCII.GetString(bytes, 0, read) != token) return;
            }
            foreach (var file in EvidenceFiles.Concat(new[] { name + ".partial", name }))
            {
                var path = Path.Combine(folder, file); RejectLinks(path); File.Delete(path);
            }
            File.Delete(Path.Combine(folder, Marker));
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Keep evidence when cleanup cannot be proven safe. */ }
    }
}
