namespace InputTracker;

internal readonly record struct AppDateRangeSelection(
    DateOnly InclusiveStartDate,
    DateOnly InclusiveEndDate)
{
    public static AppDateRangeSelection Create(DateTime? selectedStart, DateTime? selectedEnd)
    {
        if (selectedStart is null || selectedEnd is null)
            throw new InvalidOperationException("请选择开始日期和结束日期。");
        DateOnly start = DateOnly.FromDateTime(selectedStart.Value);
        DateOnly end = DateOnly.FromDateTime(selectedEnd.Value);
        if (end < start)
            throw new InvalidOperationException("结束日期不能早于开始日期。");
        return new AppDateRangeSelection(start, end);
    }

    public static AppDateRangeSelection EndingToday(int inclusiveDayCount, DateOnly today)
    {
        if (inclusiveDayCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(inclusiveDayCount));
        DateOnly start;
        try
        {
            start = today.AddDays(1 - inclusiveDayCount);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidOperationException("所选快捷范围超出了支持的日期。", error);
        }
        return new AppDateRangeSelection(start, today);
    }
}
