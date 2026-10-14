using System.Globalization;

namespace TeXeed.App.AI;

internal static class AiTimestamp
{
    internal static string Full(DateTimeOffset time)
    {
        var zone = time.Offset.TotalHours.ToString("+0.##;-0.##;+0", CultureInfo.InvariantCulture);
        return time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + $" ({zone}) " + time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
    internal static string Activity(DateTimeOffset time, DateTimeOffset reference)
    {
        if (time.Offset != reference.Offset) return Full(time);
        var days = (time.Date - reference.Date).Days;
        return (days > 0 ? $"+{days}d " : days < 0 ? $"{days}d " : "") + time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
