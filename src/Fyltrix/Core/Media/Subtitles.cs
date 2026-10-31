using System.Globalization;
using System.Text.RegularExpressions;

namespace Fyltrix.Core.Media;

public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>SRT and WebVTT parsing, and finding a video's sidecar subtitle file.</summary>
public static class Subtitles
{
    private static readonly Regex Timing = new(
        @"^\s*(?<s>(\d+:)?\d{1,2}:\d{2}[\.,]\d{1,3})\s*-->\s*(?<e>(\d+:)?\d{1,2}:\d{2}[\.,]\d{1,3})", RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>|\{\\[^}]*\}", RegexOptions.Compiled);

    public static List<SubtitleCue> Parse(string text)
    {
        var cues = new List<SubtitleCue>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = Timing.Match(lines[i]);
            if (!m.Success) continue;
            var start = ParseTime(m.Groups["s"].Value);
            var end = ParseTime(m.Groups["e"].Value);
            var body = new List<string>();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++) body.Add(Tags.Replace(lines[i], "").Trim());
            if (body.Count > 0 && end > start) cues.Add(new SubtitleCue(start, end, string.Join("\n", body)));
        }
        cues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return cues;
    }

    private static TimeSpan ParseTime(string s)
    {
        s = s.Replace(',', '.');
        var parts = s.Split(':');
        double secs = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        int mins = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        int hours = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(mins) + TimeSpan.FromSeconds(secs);
    }

    /// <summary>Text showing at <paramref name="t"/>, or null. Cues are sorted by start time.</summary>
    public static string? At(IReadOnlyList<SubtitleCue> cues, TimeSpan t)
    {
        int lo = 0, hi = cues.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (cues[mid].Start <= t) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        var parts = new List<string>();
        for (int i = found; i >= 0 && i > found - 4; i--)
            if (cues[i].Start <= t && t < cues[i].End) parts.Insert(0, cues[i].Text);
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    /// <summary>"movie.srt", "movie.en.srt", "movie.vtt" next to "movie.mp4".</summary>
    public static string? FindSidecar(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (dir == null || !Directory.Exists(dir)) return null;
        foreach (var ext in new[] { ".vtt", ".srt" })
        {
            var exact = Path.Combine(dir, stem + ext);
            if (File.Exists(exact)) return exact;
        }
        try
        {
            return Directory.EnumerateFiles(dir, stem + ".*")
                .Where(f => f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Length).FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
