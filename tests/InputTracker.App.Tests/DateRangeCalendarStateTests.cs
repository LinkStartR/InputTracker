namespace InputTracker.App.Tests;

public sealed class DateRangeCalendarStateTests
{
    [Fact]
    public void TwoDateClicksCreateAnInclusiveRangeRegardlessOfOrder()
    {
        var state = new DateRangeCalendarState(
            new DateOnly(2026, 9, 18),
            new DateOnly(2026, 9, 18),
            new DateOnly(2026, 9, 30));

        state.SelectDate(new DateOnly(2026, 9, 14));
        state.SelectDate(new DateOnly(2026, 9, 10));

        Assert.Equal(new DateOnly(2026, 9, 10), state.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 14), state.EndDate);
    }

    [Fact]
    public void PresetsAndMonthCellsRespectTodayAsTheMaximumDate()
    {
        var state = new DateRangeCalendarState(
            new DateOnly(2026, 9, 18),
            new DateOnly(2026, 9, 18),
            new DateOnly(2026, 9, 18));

        state.ApplyPreset(7);
        IReadOnlyList<DateRangeDayCell> cells = state.BuildMonth(new DateOnly(2026, 9, 1));

        Assert.Equal(new DateOnly(2026, 9, 12), state.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 18), state.EndDate);
        Assert.Equal(42, cells.Count);
        Assert.True(cells.Single(cell => cell.Date == new DateOnly(2026, 9, 18)).IsEnabled);
        Assert.False(cells.Single(cell => cell.Date == new DateOnly(2026, 9, 19)).IsEnabled);
        Assert.All(cells.Where(cell => cell.Date >= state.StartDate && cell.Date <= state.EndDate),
            cell => Assert.True(cell.IsInRange));
        Assert.Throws<InvalidOperationException>(() => state.SelectDate(new DateOnly(2026, 9, 19)));
    }
}
