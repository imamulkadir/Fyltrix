namespace Fyltrix.Core;

/// <summary>Subsequence matching for the command palette ("nfol" matches "New folder"), ranked like editor palettes.</summary>
public static class Fuzzy
{
    /// <summary>Higher is better; null when the query letters don't all appear in order.</summary>
    public static int? Score(string query, string text)
    {
        query = query.Trim();
        if (query.Length == 0) return 0;
        int score = 0, ti = 0, run = 0;
        int firstIndex = -1;
        foreach (var qc in query)
        {
            if (qc == ' ') { run = 0; continue; }
            int found = -1;
            for (int i = ti; i < text.Length; i++)
                if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(qc)) { found = i; break; }
            if (found < 0) return null;
            if (firstIndex < 0) firstIndex = found;
            bool wordStart = found == 0 || !char.IsLetterOrDigit(text[found - 1]) || (char.IsUpper(text[found]) && char.IsLower(text[found - 1]));
            run = found == ti && ti > 0 ? run + 1 : 1;
            score += 1 + (wordStart ? 8 : 0) + (run > 1 ? 4 * run : 0) - Math.Min(found - ti, 6);
            ti = found + 1;
        }
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score += 30;
        else if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 15;
        return score - firstIndex - text.Length / 10;
    }
}
