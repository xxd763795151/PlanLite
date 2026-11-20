using System.Globalization;

namespace PlanLite.Core.Utilities;

public static class WeekHelper
{
    /// <summary>
    /// Returns the Monday of the natural week (Monday–Sunday) containing <paramref name="date"/>.
    /// </summary>
    public static DateOnly GetWeekStart(DateOnly date)
    {
        var dayOfWeek = date.DayOfWeek;
        var offsetFromMonday = dayOfWeek == DayOfWeek.Sunday ? 6 : (int)dayOfWeek - 1;
        return date.AddDays(-offsetFromMonday);
    }

    /// <summary>
    /// Returns the Sunday of the natural week starting on <paramref name="weekStart"/>.
    /// </summary>
    public static DateOnly GetWeekEnd(DateOnly weekStart)
    {
        return GetWeekStart(weekStart).AddDays(6);
    }

    public static (int IsoYear, int WeekNumber) GetIsoWeekInfo(DateOnly date)
    {
        var dt = date.ToDateTime(TimeOnly.MinValue);
        return (ISOWeek.GetYear(dt), ISOWeek.GetWeekOfYear(dt));
    }

    public static string FormatWeekLabel(DateOnly dateInWeek)
    {
        var start = GetWeekStart(dateInWeek);
        var end = start.AddDays(6);
        var (isoYear, weekNum) = GetIsoWeekInfo(start);
        return $"{isoYear}年第{weekNum}周 ({start:MM/dd} - {end:MM/dd})";
    }

    public static string FormatShortWeekBadge(DateOnly dateInWeek)
    {
        var (_, weekNum) = GetIsoWeekInfo(GetWeekStart(dateInWeek));
        return $"第{weekNum}周";
    }

    public static string FormatChineseDayOfWeek(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        DayOfWeek.Sunday => "周日",
        _ => string.Empty
    };

    public static string FormatChineseDateWithDay(DateOnly date)
    {
        return $"{date:yyyy年MM月dd日} {FormatChineseDayOfWeek(date.DayOfWeek)}";
    }

    public static string ToIsoDateString(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly ParseIsoDateString(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
