using System.Globalization;

namespace Fyltrix.Core;

/// <summary>
/// Explorer's "Group by Date modified" buckets (as in the Downloads folder): Today, Yesterday, Earlier this week, Last week,
/// Earlier this month, Last month, Earlier this year, A long time ago. Weeks start on the user's first day of the week.
/// </summary>
public static class DateGroups
{
    /// <summary>Group names by rank; rank 0 is the newest.</summary>
    public static readonly string[] Names =
    {
        "In the future", "Today", "Yesterday", "Earlier this week", "Last week", "Earlier this month", "Last month", "Earlier this year", "A long time ago",
    };

    public static int Rank(DateTime modified, DateTime now) => Rank(modified, now, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);

    public static int Rank(DateTime modified, DateTime now, DayOfWeek firstDayOfWeek)
    {
        var today = now.Date;
        if (modified == DateTime.MinValue) return 8;           // unknown date
        if (modified >= today.AddDays(1)) return 0;            // clock skew, files from another machine
        if (modified >= today) return 1;
        if (modified >= today.AddDays(-1)) return 2;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
        if (modified >= weekStart) return 3;
        if (modified >= weekStart.AddDays(-7)) return 4;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        if (modified >= monthStart) return 5;
        if (modified >= monthStart.AddMonths(-1)) return 6;
        if (modified >= new DateTime(today.Year, 1, 1)) return 7;
        return 8;
    }

    public static string Name(DateTime modified, DateTime now) => Names[Rank(modified, now)];
}
