using System.Globalization;
using System.Text.RegularExpressions;

namespace Stashographer.Services.Config;

public enum RegionalDateOrder
{
    DayMonthYear,
    MonthDayYear,
    YearMonthDay
}

/// <summary>Regional assumptions supplied to deterministic parsers and AI intake.</summary>
public sealed class InventoryRegionalOptions
{
    public string DefaultCurrency { get; set; } = "GBP";
    public RegionalDateOrder DateOrder { get; set; } = RegionalDateOrder.DayMonthYear;
    public string CultureName { get; set; } = "en-GB";
    public string TimeZoneId { get; set; } = "Europe/London";

    public DateOnly Today()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
    }
}

/// <summary>Parses visibly printed numeric dates using an explicit regional date order.</summary>
public static partial class RegionalDateParser
{
    [GeneratedRegex(@"(?<!\d)(?<a>\d{1,4})[./-](?<b>\d{1,2})[./-](?<c>\d{1,4})(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumericDate();

    public static bool TryParseVisibleDate(
        string? rawText, RegionalDateOrder order, DateOnly today, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        var match = NumericDate().Match(rawText);
        if (!match.Success
            || !int.TryParse(match.Groups["a"].Value, out var a)
            || !int.TryParse(match.Groups["b"].Value, out var b)
            || !int.TryParse(match.Groups["c"].Value, out var c)) return false;

        int day, month, year;
        switch (order)
        {
            case RegionalDateOrder.MonthDayYear:
                (month, day, year) = (a, b, c);
                break;
            case RegionalDateOrder.YearMonthDay:
                (year, month, day) = (a, b, c);
                break;
            default:
                (day, month, year) = (a, b, c);
                break;
        }

        year = ExpandYear(year, today.Year);
        return DateOnly.TryParseExact($"{year:D4}-{month:D2}-{day:D2}", "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static int ExpandYear(int year, int currentYear)
    {
        if (year >= 100) return year;
        var expanded = currentYear / 100 * 100 + year;
        if (expanded > currentYear + 20) expanded -= 100;
        else if (expanded < currentYear - 80) expanded += 100;
        return expanded;
    }
}
