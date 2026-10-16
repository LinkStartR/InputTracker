using System.Globalization;
using System.Windows;
using WpfButton = System.Windows.Controls.Button;

namespace InputTracker;

internal sealed record DateRangeDayCell(
    DateOnly Date,
    int DayNumber,
    bool IsInDisplayMonth,
    bool IsInRange,
    bool IsEndpoint,
    bool IsToday,
    bool IsEnabled);

internal sealed class DateRangeCalendarState
{
    private DateOnly? _selectionAnchor;

    public DateRangeCalendarState(DateOnly startDate, DateOnly endDate, DateOnly maximumDate)
    {
        if (endDate < startDate) throw new ArgumentOutOfRangeException(nameof(endDate));
        if (endDate > maximumDate) throw new ArgumentOutOfRangeException(nameof(endDate));
        StartDate = startDate;
        EndDate = endDate;
        MaximumDate = maximumDate;
    }

    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public DateOnly MaximumDate { get; }
    public bool IsWaitingForEndDate => _selectionAnchor is not null;

    public void SelectDate(DateOnly date)
    {
        if (date > MaximumDate)
            throw new InvalidOperationException("不能选择今天之后的日期。");
        if (_selectionAnchor is null)
        {
            _selectionAnchor = date;
            StartDate = date;
            EndDate = date;
            return;
        }

        StartDate = date < _selectionAnchor.Value ? date : _selectionAnchor.Value;
        EndDate = date > _selectionAnchor.Value ? date : _selectionAnchor.Value;
        _selectionAnchor = null;
    }

    public void ApplyPreset(int inclusiveDayCount)
    {
        AppDateRangeSelection range = AppDateRangeSelection.EndingToday(inclusiveDayCount, MaximumDate);
        StartDate = range.InclusiveStartDate;
        EndDate = range.InclusiveEndDate;
        _selectionAnchor = null;
    }

    public IReadOnlyList<DateRangeDayCell> BuildMonth(DateOnly displayMonth)
    {
        DateOnly month = new(displayMonth.Year, displayMonth.Month, 1);
        DateOnly firstCell = month.AddDays(-(int)month.DayOfWeek);
        var result = new DateRangeDayCell[42];
        for (int index = 0; index < result.Length; index++)
        {
            DateOnly date = firstCell.AddDays(index);
            result[index] = new DateRangeDayCell(
                date,
                date.Day,
                date.Month == month.Month && date.Year == month.Year,
                date >= StartDate && date <= EndDate,
                date == StartDate || date == EndDate,
                date == MaximumDate,
                date <= MaximumDate);
        }
        return result;
    }
}

public partial class DateRangePicker : System.Windows.Controls.UserControl
{
    private DateOnly _startDate;
    private DateOnly _endDate;
    private DateOnly _maximumDate;
    private DateOnly _displayMonth;
    private DateRangeCalendarState? _pending;

    public DateRangePicker()
    {
        InitializeComponent();
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        _startDate = today;
        _endDate = today;
        _maximumDate = today;
        _displayMonth = new DateOnly(today.Year, today.Month, 1);
        UpdateRangeText();
    }

    public event EventHandler? RangeChanged;

    public DateOnly InclusiveStartDate => _startDate;
    public DateOnly InclusiveEndDate => _endDate;
    public DateOnly MaximumDate => _maximumDate;
    public double PopupHorizontalOffset { get; set; }

    public void SetRange(DateOnly inclusiveStartDate, DateOnly inclusiveEndDate, DateOnly maximumDate)
    {
        if (inclusiveEndDate < inclusiveStartDate)
            throw new ArgumentOutOfRangeException(nameof(inclusiveEndDate));
        if (inclusiveEndDate > maximumDate)
            throw new ArgumentOutOfRangeException(nameof(inclusiveEndDate));
        _startDate = inclusiveStartDate;
        _endDate = inclusiveEndDate;
        _maximumDate = maximumDate;
        UpdateRangeText();
    }

    private void OpenRangeButton_Click(object sender, RoutedEventArgs e)
    {
        PickerPopup.HorizontalOffset = PopupHorizontalOffset;
        DateOnly currentToday = DateOnly.FromDateTime(DateTime.Today);
        if (currentToday > _maximumDate) _maximumDate = currentToday;
        _pending = new DateRangeCalendarState(_startDate, _endDate, _maximumDate);
        _displayMonth = new DateOnly(_endDate.Year, _endDate.Month, 1);
        RefreshPopup();
        PickerPopup.IsOpen = true;
    }

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is null
            || sender is not FrameworkElement { Tag: string raw }
            || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int days)) return;
        _pending.ApplyPreset(days);
        _displayMonth = new DateOnly(_pending.EndDate.Year, _pending.EndDate.Month, 1);
        RefreshPopup();
    }

    private void DayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is null || sender is not FrameworkElement { DataContext: DateRangeDayCell cell }) return;
        _pending.SelectDate(cell.Date);
        if (!cell.IsInDisplayMonth)
            _displayMonth = new DateOnly(cell.Date.Year, cell.Date.Month, 1);
        RefreshPopup();
    }

    private void PreviousMonthButton_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(-1);
        RefreshPopup();
    }

    private void NextMonthButton_Click(object sender, RoutedEventArgs e)
    {
        DateOnly next = _displayMonth.AddMonths(1);
        DateOnly maximumMonth = new(_maximumDate.Year, _maximumDate.Month, 1);
        if (next <= maximumMonth)
        {
            _displayMonth = next;
            RefreshPopup();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _pending = null;
        PickerPopup.IsOpen = false;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is null) return;
        bool changed = _startDate != _pending.StartDate || _endDate != _pending.EndDate;
        _startDate = _pending.StartDate;
        _endDate = _pending.EndDate;
        _pending = null;
        PickerPopup.IsOpen = false;
        UpdateRangeText();
        if (changed) RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshPopup()
    {
        if (_pending is null) return;
        MonthTitleText.Text = _displayMonth.ToString("yyyy年M月", CultureInfo.GetCultureInfo("zh-CN"));
        StartDateText.Text = FormatFullDate(_pending.StartDate);
        EndDateText.Text = FormatFullDate(_pending.EndDate);
        SelectionHintText.Text = _pending.IsWaitingForEndDate
            ? "再点一个日期，完成范围选择。"
            : "点一下选择开始，再点一下选择结束。";
        DayItems.ItemsSource = _pending.BuildMonth(_displayMonth);
        DateOnly maximumMonth = new(_maximumDate.Year, _maximumDate.Month, 1);
        NextMonthButton.IsEnabled = _displayMonth < maximumMonth;
        UpdatePresetButtons();
    }

    private void UpdatePresetButtons()
    {
        if (_pending is null) return;
        foreach (WpfButton button in new[]
                 { TodayPresetButton, SevenDayPresetButton, FourteenDayPresetButton, ThirtyDayPresetButton })
        {
            bool selected = int.TryParse(button.Tag?.ToString(), out int days)
                && _pending.EndDate == _maximumDate
                && _pending.StartDate == _maximumDate.AddDays(1 - days);
            button.SetResourceReference(BackgroundProperty, selected ? "AccentSoftBrush" : "CardBackgroundBrush");
            button.SetResourceReference(ForegroundProperty, selected ? "AccentBrush" : "PrimaryTextBrush");
            button.SetResourceReference(BorderBrushProperty, selected ? "AccentBrush" : "BorderBrush");
        }
    }

    private void UpdateRangeText()
    {
        RangeText.Text = _startDate == _endDate
            ? FormatFullDate(_startDate)
            : $"{FormatCompactDate(_startDate)}  —  {FormatCompactDate(_endDate)}";
    }

    private static string FormatFullDate(DateOnly date) =>
        date.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("zh-CN"));

    private static string FormatCompactDate(DateOnly date) =>
        date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
}
