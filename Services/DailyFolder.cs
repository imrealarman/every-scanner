using System.Globalization;

namespace SScanner.Services;

/// <summary>Names the per-day folder, e.g. 1405-08-05 (Shamsi) or 2026-10-27 (Gregorian).</summary>
public static class DailyFolder
{
    public static string Name(DateStyle style, DateTime date)
    {
        if (style == DateStyle.Gregorian)
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var calendar = new PersianCalendar();
        return string.Create(CultureInfo.InvariantCulture,
            $"{calendar.GetYear(date):0000}-{calendar.GetMonth(date):00}-{calendar.GetDayOfMonth(date):00}");
    }
}
