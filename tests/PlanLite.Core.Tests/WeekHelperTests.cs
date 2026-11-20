using PlanLite.Core.Utilities;
using Xunit;

namespace PlanLite.Core.Tests;

public sealed class WeekHelperTests
{
    [Theory]
    [InlineData("2026-11-16", "2026-11-16")] // Monday
    [InlineData("2026-11-19", "2026-11-16")] // Thursday
    [InlineData("2026-11-22", "2026-11-16")] // Sunday
    [InlineData("2024-02-29", "2024-02-26")] // Leap day Thursday
    [InlineData("2026-12-31", "2026-12-28")] // Year-end Thursday
    [InlineData("2027-01-01", "2026-12-28")] // Cross-year Friday belongs to same natural week
    [InlineData("2027-01-03", "2026-12-28")] // Cross-year Sunday
    public void GetWeekStart_ReturnsMondayOfNaturalWeek(string inputIso, string expectedMondayIso)
    {
        var date = DateOnly.Parse(inputIso);
        var expected = DateOnly.Parse(expectedMondayIso);

        var actual = WeekHelper.GetWeekStart(date);

        Assert.Equal(expected, actual);
        Assert.Equal(DayOfWeek.Monday, actual.DayOfWeek);
    }

    [Fact]
    public void GetWeekEnd_ReturnsSundaySixDaysAfterMonday()
    {
        var thursday = new DateOnly(2026, 11, 19);
        var weekEnd = WeekHelper.GetWeekEnd(thursday);

        Assert.Equal(new DateOnly(2026, 11, 22), weekEnd);
        Assert.Equal(DayOfWeek.Sunday, weekEnd.DayOfWeek);
    }

    [Fact]
    public void FormatWeekLabel_IncludesIsoWeekAndRange()
    {
        var label = WeekHelper.FormatWeekLabel(new DateOnly(2026, 11, 19));
        Assert.Equal("2026年第47周 (11/16 - 11/22)", label);
    }

    [Fact]
    public void FormatChineseDateWithDay_FormatsExpectedText()
    {
        var formatted = WeekHelper.FormatChineseDateWithDay(new DateOnly(2026, 11, 19));
        Assert.Equal("2026年11月19日 周四", formatted);
    }
}
