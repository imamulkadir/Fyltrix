using System.Text;

namespace Fyltrix.Launcher;

/// <summary>Shared startup exclusion. Compatible layouts carry this capability before their first managed update.</summary>
public static class UpdateProtocol
{
    public const string CapabilityFile = "UPDATE_PROTOCOL";
    public const string Capability = "Fyltrix.Update/1\nFileLens.Profile/1\n";
    public const string GateFile = "startup.lock";
    public const string JournalFile = "update-transaction.json";

    public static bool Supported(string root)
    {
        try
        {
            using var file = File.OpenRead(Path.Combine(root, CapabilityFile));
            var bytes = new byte[128]; int read = file.ReadAtLeast(bytes, bytes.Length, false);
            return Encoding.UTF8.GetString(bytes, 0, read) == Capability && File.Exists(Path.Combine(root, GateFile));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public static FileStream? EnterShared(string root)
    {
        // Legacy layouts remain launchable; only protocol-capable layouts can be managed by the new worker.
        if (!File.Exists(Path.Combine(root, CapabilityFile))) return null;
        if (!Supported(root)) throw new IOException("The update startup protocol is incomplete.");
        var lease = new FileStream(Path.Combine(root, GateFile), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (File.Exists(Path.Combine(root, JournalFile))) { lease.Dispose(); throw new IOException("An interrupted update needs recovery before startup."); }
        return lease;
    }
    public static string? PayloadRoot(string executableDirectory)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(executableDirectory));
        string name = Path.GetFileName(folder);
        if (!name.StartsWith("app-", StringComparison.Ordinal) || !ReleaseLayout.IsVersion(name[4..])) return null;
        string? root = Path.GetDirectoryName(folder);
        return root != null && (File.Exists(Path.Combine(root, CapabilityFile)) || File.Exists(Path.Combine(root, "current.json")) && File.Exists(Path.Combine(root, "Fyltrix.exe"))) ? root : null;
    }
}
