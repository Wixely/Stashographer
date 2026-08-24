using Stashographer.Services.Config;

namespace Stashographer.Tests;

public class RegionalDateParserTests
{
    private static readonly DateOnly Today = new(2026, 8, 24);

    [Theory]
    [InlineData(RegionalDateOrder.DayMonthYear, 2026, 4, 3)]
    [InlineData(RegionalDateOrder.MonthDayYear, 2026, 3, 4)]
    public void Ambiguous_numeric_date_obeys_configured_order(
        RegionalDateOrder order, int year, int month, int day)
    {
        var parsed = RegionalDateParser.TryParseVisibleDate(
            "BEST BEFORE 03/04/26", order, Today, out var date);

        Assert.True(parsed);
        Assert.Equal(new DateOnly(year, month, day), date);
    }

    [Fact]
    public void Year_first_order_requires_the_year_in_the_first_position()
    {
        Assert.True(RegionalDateParser.TryParseVisibleDate(
            "EXP 2027-01-09", RegionalDateOrder.YearMonthDay, Today, out var date));
        Assert.Equal(new DateOnly(2027, 1, 9), date);
    }

    [Fact]
    public void Invalid_calendar_date_is_rejected()
    {
        Assert.False(RegionalDateParser.TryParseVisibleDate(
            "31/02/2027", RegionalDateOrder.DayMonthYear, Today, out _));
    }
}
