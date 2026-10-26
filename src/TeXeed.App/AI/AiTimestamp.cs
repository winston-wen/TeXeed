using System.Globalization;

namespace TeXeed.App.AI;

internal static class AiTimestamp
{
    internal static string Full(DateTimeOffset time)
    {
        var zone = time.Offset.TotalHours.ToString("+0.##;-0.##;+0", CultureInfo.InvariantCulture);
        return time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + $" ({zone}) " + time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
    internal static string Delay(DateTimeOffset sent, DateTimeOffset? answered)
    {
        if (answered is null) return "尚未答复";
        var seconds = (long)Math.Round((answered.Value - sent).TotalSeconds);
        if (seconds < 0) return Full(answered.Value);
        if (seconds == 0) return "同秒";
        var parts = new List<string>();
        foreach (var (size, unit) in new[] { (86400L, "天"), (3600L, "小时"), (60L, "分"), (1L, "秒") })
        { var n = seconds / size; seconds %= size; if (n > 0) parts.Add($"{n} {unit}"); }
        return string.Join(" ", parts) + "后";
    }
    internal static string Activity(DateTimeOffset time, DateTimeOffset reference)
    {
        if (time.Offset != reference.Offset) return Full(time);
        var days = (time.Date - reference.Date).Days;
        return (days > 0 ? $"+{days}d " : days < 0 ? $"{days}d " : "") + time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
