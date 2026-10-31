using Fyltrix.Launcher;

namespace Fyltrix.Updates;

/// <summary>Uses the existing layout grammar and Windows version bounds. No metadata suffix in layout names.</summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    public string Text { get; }
    public bool IsPrerelease => Text.Contains('-');
    private readonly int[] core;
    private ReleaseVersion(string text, int[] core) { Text = text; this.core = core; }

    public static ReleaseVersion? Parse(string? text)
    {
        if (!ReleaseLayout.IsVersion(text)) return null;
        var core = text!.Split('-')[0].Split('.').Select(int.Parse).ToArray();
        return core.Any(n => n > 65534) ? null : new(text, core);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other == null) return 1;
        for (int i = 0; i < core.Length; i++)
            if (core[i] != other.core[i]) return core[i].CompareTo(other.core[i]);
        if (!IsPrerelease || !other.IsPrerelease) return other.IsPrerelease.CompareTo(IsPrerelease);
        var left = Text[(Text.IndexOf('-') + 1)..].Split('.');
        var right = other.Text[(other.Text.IndexOf('-') + 1)..].Split('.');
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            bool a = left[i].All(char.IsAsciiDigit), b = right[i].All(char.IsAsciiDigit);
            int order = a && b ? left[i].Length.CompareTo(right[i].Length) : a != b ? (a ? -1 : 1) : 0;
            if (order == 0) order = string.CompareOrdinal(left[i], right[i]);
            if (order != 0) return order;
        }
        return left.Length.CompareTo(right.Length);
    }
    public override string ToString() => Text;
}
