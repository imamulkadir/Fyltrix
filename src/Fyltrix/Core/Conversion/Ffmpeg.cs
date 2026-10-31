using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fyltrix.Core.Conversion;

public enum MediaQuality { High, Balanced, Small }

public sealed record MediaPreset(string Id, string Label, string Extension, bool AudioOnly);

/// <summary>
/// Audio/video conversion through a user-installed FFmpeg. Fyltrix never downloads it; <see cref="Locate"/> looks
/// in the configured path, PATH and the usual install folders. Output goes to a temporary file that is renamed on
/// success and deleted on failure or cancellation.
/// </summary>
public static class Ffmpeg
{
    public static readonly IReadOnlyList<MediaPreset> VideoPresets = new[]
    {
        new MediaPreset("mp4", "MP4 (H.264 + AAC) — plays everywhere", ".mp4", false),
        new MediaPreset("webm", "WebM (VP9 + Opus) — web friendly", ".webm", false),
        new MediaPreset("mkv", "MKV (H.264 + AAC)", ".mkv", false),
        new MediaPreset("mov", "MOV (H.264 + AAC)", ".mov", false),
        new MediaPreset("avi", "AVI (MPEG-4 + MP3) — older players", ".avi", false),
        new MediaPreset("gif", "Animated GIF (no sound)", ".gif", false),
    };

    public static readonly IReadOnlyList<MediaPreset> AudioPresets = new[]
    {
        new MediaPreset("mp3", "MP3", ".mp3", true),
        new MediaPreset("m4a", "AAC (.m4a)", ".m4a", true),
        new MediaPreset("flac", "FLAC — lossless", ".flac", true),
        new MediaPreset("wav", "WAV — uncompressed", ".wav", true),
        new MediaPreset("ogg", "Ogg Vorbis", ".ogg", true),
        new MediaPreset("opus", "Opus", ".opus", true),
    };

    public static MediaPreset? Preset(string id) => VideoPresets.Concat(AudioPresets).FirstOrDefault(p => p.Id == id);

    /// <summary>Finds ffmpeg.exe, or returns null. Only existing files are returned; nothing is installed.</summary>
    public static string? Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var c = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (Directory.Exists(c)) c = Path.Combine(c, "ffmpeg.exe");
            if (File.Exists(c)) return c;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim()), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "ffmpeg.exe"),
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
        };
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        try
        {
            if (Directory.Exists(packages))
                candidates.AddRange(Directory.EnumerateDirectories(packages, "*FFmpeg*").SelectMany(d =>
                    Directory.EnumerateFiles(d, "ffmpeg.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The ffmpeg command line (arguments only) for a conversion.</summary>
    public static List<string> BuildArgs(string input, string output, MediaPreset preset, MediaQuality quality, int? maxHeight)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };
        int crf264 = quality switch { MediaQuality.High => 18, MediaQuality.Small => 28, _ => 23 };
        int crfVp9 = quality switch { MediaQuality.High => 24, MediaQuality.Small => 38, _ => 32 };
        string aac = quality switch { MediaQuality.High => "256k", MediaQuality.Small => "96k", _ => "160k" };
        // keep the aspect ratio; the width must be even for H.264/VP9
        string? scale = maxHeight is { } h ? $"scale=-2:'min({h},ih)'" : null;

        switch (preset.Id)
        {
            case "mp4" or "mkv" or "mov":
                if (scale != null) a.AddRange(new[] { "-vf", scale });
                a.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", crf264.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", aac });
                if (preset.Id != "mkv") a.AddRange(new[] { "-movflags", "+faststart" });
                break;
            case "webm":
                if (scale != null) a.AddRange(new[] { "-vf", scale });
                a.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", crfVp9.ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", quality == MediaQuality.Small ? "64k" : "128k" });
                break;
            case "avi":
                if (scale != null) a.AddRange(new[] { "-vf", scale });
                a.AddRange(new[] { "-c:v", "mpeg4", "-q:v", quality switch { MediaQuality.High => "3", MediaQuality.Small => "9", _ => "5" }, "-c:a", "libmp3lame", "-q:a", "4" });
                break;
            case "gif":
                int fps = quality == MediaQuality.High ? 15 : 10;
                string size = maxHeight is { } gh ? $"scale=-2:'min({gh},ih)':flags=lanczos," : "scale='min(480,iw)':-2:flags=lanczos,";
                a.AddRange(new[] { "-vf", $"fps={fps},{size}split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse", "-loop", "0", "-an" });
                break;
            case "mp3":
                a.AddRange(new[] { "-vn", "-c:a", "libmp3lame", "-q:a", quality switch { MediaQuality.High => "0", MediaQuality.Small => "6", _ => "2" } });
                break;
            case "m4a":
                a.AddRange(new[] { "-vn", "-c:a", "aac", "-b:a", aac });
                break;
            case "flac":
                a.AddRange(new[] { "-vn", "-c:a", "flac" });
                break;
            case "wav":
                a.AddRange(new[] { "-vn", "-c:a", "pcm_s16le" });
                break;
            case "ogg":
                a.AddRange(new[] { "-vn", "-c:a", "libvorbis", "-q:a", quality switch { MediaQuality.High => "8", MediaQuality.Small => "3", _ => "5" } });
                break;
            case "opus":
                a.AddRange(new[] { "-vn", "-c:a", "libopus", "-b:a", quality switch { MediaQuality.High => "192k", MediaQuality.Small => "64k", _ => "128k" } });
                break;
            default:
                throw new NotSupportedException("Unknown preset " + preset.Id);
        }
        // write to the temporary name with the right container (ffmpeg picks the muxer from the extension otherwise)
        a.AddRange(new[] { "-f", preset.Id switch { "mkv" => "matroska", "m4a" => "ipod", "opus" => "opus", "ogg" => "ogg", _ => preset.Id }, output });
        return a;
    }

    private static readonly Regex DurationRx = new(@"Duration:\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"time=\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)", RegexOptions.Compiled);

    public static TimeSpan? ParseDuration(string line) => Parse(DurationRx.Match(line));
    public static TimeSpan? ParseTime(string line) => Parse(TimeRx.Match(line));

    private static TimeSpan? Parse(Match m) => m.Success
        ? TimeSpan.FromHours(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) + TimeSpan.FromMinutes(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
          + TimeSpan.FromSeconds(double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))
        : null;

    public static async Task ConvertAsync(string ffmpeg, string input, string output, MediaPreset preset, MediaQuality quality, int? maxHeight,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (File.Exists(output)) throw new IOException($"'{Path.GetFileName(output)}' already exists.");
        var temp = Path.Combine(Path.GetDirectoryName(output)!, $".~{Guid.NewGuid():N}{preset.Extension}");
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in BuildArgs(input, temp, preset, quality, maxHeight)) psi.ArgumentList.Add(arg);
        using var p = new Process { StartInfo = psi };
        TimeSpan? duration = null;
        var tail = new Queue<string>();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (tail) { tail.Enqueue(e.Data); if (tail.Count > 12) tail.Dequeue(); }
            duration ??= ParseDuration(e.Data);
            if (duration is { TotalSeconds: > 0 } d && ParseTime(e.Data) is { } t) progress?.Report(Math.Clamp(t.TotalSeconds / d.TotalSeconds, 0, 1));
        };
        try
        {
            p.Start();
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
                await p.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if (p.ExitCode != 0)
            {
                string msg;
                lock (tail) msg = string.Join("\n", tail.Where(l => !l.StartsWith("  ")).TakeLast(4));
                throw new IOException("FFmpeg couldn't convert the file:\n" + msg);
            }
            File.Move(temp, output);
            progress?.Report(1);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }
}
