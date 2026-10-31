using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Fyltrix.Core;

/// <summary>
/// The one place errors are written down: %AppData%\FileLens\error.log (rolled over at 2 MB to error.old.log), plus a
/// separate crash-*.txt report under crashes\ when the process is about to die. Entries carry the time, app version,
/// component, operation, exception type, message, stack and inner exceptions. Share tokens are masked. Never throws.
/// </summary>
public static class Diagnostics
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int MaxCrashReports = 20;
    private static readonly object Gate = new();
    private static readonly Regex ShareToken = new(@"/(s|share)/[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled);

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

    public static string LogPath => Path.Combine(AppSettings.Folder, "error.log");
    public static string CrashFolder => Path.Combine(AppSettings.Folder, "crashes");

    /// <summary>Records a handled failure. <paramref name="operation"/> says what was being done (e.g. the file being previewed).</summary>
    public static void Log(string component, Exception ex, string? operation = null) => Write(Format("ERROR", component, operation, ex.ToString()));

    /// <summary>Records a notable event that isn't an exception (e.g. a media decoder refusing a file).</summary>
    public static void Info(string component, string message, string? operation = null) => Write(Format("INFO", component, operation, message));

    /// <summary>Writes a crash report for an exception that is about to end the process, and logs it. Returns the report path.</summary>
    public static string? Crash(string component, Exception ex)
    {
        var text = Format("FATAL", component, null, ex.ToString());
        Write(text);
        try
        {
            Directory.CreateDirectory(CrashFolder);
            var file = Path.Combine(CrashFolder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            var sb = new StringBuilder(text);
            sb.AppendLine($"OS: {Environment.OSVersion} · .NET {Environment.Version} · {(Environment.Is64BitProcess ? "64" : "32")}-bit");
            sb.AppendLine($"Working set: {Environment.WorkingSet >> 20} MB · GC heap: {GC.GetTotalMemory(false) >> 20} MB");
            File.WriteAllText(file, sb.ToString());
            foreach (var old in new DirectoryInfo(CrashFolder).GetFiles("crash-*.txt").OrderByDescending(f => f.Name).Skip(MaxCrashReports))
                old.Delete();
            return file;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string Redact(string text) => ShareToken.Replace(text, "/$1/<token>");

    private static string Format(string level, string component, string? operation, string detail)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {level} {component} · {ProductBranding.Name} {Version} · thread {Environment.CurrentManagedThreadId}");
        if (!string.IsNullOrEmpty(operation)) sb.AppendLine("Operation: " + operation);
        sb.AppendLine(detail);
        return Redact(sb.ToString()) + Environment.NewLine;
    }

    private static void Write(string entry)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxLogBytes) File.Move(LogPath, Path.Combine(AppSettings.Folder, "error.old.log"), overwrite: true);
                File.AppendAllText(LogPath, entry);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* logging must never take the app down */ }
    }
}
