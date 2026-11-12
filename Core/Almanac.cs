using System.Globalization;

namespace Piztki.Core;

/// <summary>Date and sky facts for the calendar tab: world clocks, the moon's phase, and countdowns.</summary>
public static class Almanac
{
    /// <summary>Cities offered for the two world clocks, with their Windows time zone ids.</summary>
    public static readonly (string Name, string Zone)[] Cities =
    {
        ("Los Angeles", "Pacific Standard Time"),
        ("Chicago", "Central Standard Time"),
        ("New York", "Eastern Standard Time"),
        ("São Paulo", "E. South America Standard Time"),
        ("London", "GMT Standard Time"),
        ("Paris", "Romance Standard Time"),
        ("Berlin", "W. Europe Standard Time"),
        ("Tel Aviv", "Israel Standard Time"),
        ("Moscow", "Russian Standard Time"),
        ("Dubai", "Arabian Standard Time"),
        ("Mumbai", "India Standard Time"),
        ("Singapore", "Singapore Standard Time"),
        ("Hong Kong", "China Standard Time"),
        ("Seoul", "Korea Standard Time"),
        ("Tokyo", "Tokyo Standard Time"),
        ("Sydney", "AUS Eastern Standard Time"),
    };

    /// <summary>The city after (or before) the given one in the list, wrapping around.</summary>
    public static string NextCity(string current, int step)
    {
        int i = Array.FindIndex(Cities, c => c.Name == current);
        if (i < 0) i = 0;
        return Cities[((i + step) % Cities.Length + Cities.Length) % Cities.Length].Name;
    }

    /// <summary>The time in that city now, plus "+1d" / "-1d" when it is already a different date there.</summary>
    public static string TimeIn(string city, string format)
    {
        int i = Array.FindIndex(Cities, c => c.Name == city);
        if (i < 0) return "";
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(Cities[i].Zone);
            var there = TimeZoneInfo.ConvertTime(DateTime.Now, zone);
            int dayShift = (there.Date - DateTime.Now.Date).Days;
            string suffix = dayShift > 0 ? "  +1d" : dayShift < 0 ? "  -1d" : "";
            return there.ToString(format, CultureInfo.InvariantCulture) + suffix;
        }
        catch
        {
            return ""; // this copy of Windows doesn't know the zone
        }
    }

    /// <summary>The moon's phase name and how much of it is lit, from the average length of a lunar month.</summary>
    public static (string name, int litPercent) Moon(DateTime utcNow)
    {
        const double LunarMonth = 29.530588853;
        var knownNewMoon = new DateTime(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);

        double age = (utcNow - knownNewMoon).TotalDays % LunarMonth;
        if (age < 0) age += LunarMonth;
        double phase = age / LunarMonth; // 0 = new, 0.5 = full
        int lit = (int)Math.Round((1 - Math.Cos(2 * Math.PI * phase)) / 2 * 100);

        string name = phase switch
        {
            < 0.03 or >= 0.97 => "New moon",
            < 0.22 => "Waxing crescent",
            < 0.28 => "First quarter",
            < 0.47 => "Waxing gibbous",
            < 0.53 => "Full moon",
            < 0.72 => "Waning gibbous",
            < 0.78 => "Last quarter",
            _ => "Waning crescent",
        };
        return (name, lit);
    }

    /// <summary>Reads a yyyy-MM-dd date; false for anything else.</summary>
    public static bool TryParseDate(string text, out DateTime date)
        => DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>"89 days", "Tomorrow", "Today", or "12 days ago" relative to today.</summary>
    public static string DaysUntil(DateTime date)
    {
        int days = (date.Date - DateTime.Now.Date).Days;
        return days switch
        {
            0 => "Today",
            1 => "Tomorrow",
            > 1 => $"{days} days",
            -1 => "Yesterday",
            _ => $"{-days} days ago",
        };
    }
}
