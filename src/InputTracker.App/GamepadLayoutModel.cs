namespace InputTracker;

internal enum GamepadPhysicalPosition
{
    South,
    East,
    West,
    North,
    DpadUp,
    DpadRight,
    DpadDown,
    DpadLeft,
    LeftTrigger,
    RightTrigger,
    LeftShoulder,
    RightShoulder,
    LeftStickPress,
    RightStickPress,
    View,
    Menu
}

internal enum GamepadPositionSupport
{
    Unknown,
    Unsupported,
    Supported
}

internal sealed record GamepadHeatmapPosition(
    GamepadPhysicalPosition Position,
    string Group,
    string Label,
    double X,
    double Y,
    GamepadPositionSupport Support,
    long? PressCount,
    int? HeatLevel)
{
    public string DisplayLabel => Position switch
    {
        GamepadPhysicalPosition.View => "V",
        GamepadPhysicalPosition.Menu => "M",
        _ => Label
    };

    public string CountText => Support == GamepadPositionSupport.Supported
        ? $"{PressCount.GetValueOrDefault():N0} 次"
        : Support == GamepadPositionSupport.Unknown ? "未校准" : "不支持";

    public string AccessibleLabel => Support switch
    {
        GamepadPositionSupport.Supported => $"{Group} {Label}，{PressCount.GetValueOrDefault():N0} 次",
        GamepadPositionSupport.Unsupported => $"{Group} {Label}，不支持",
        _ => $"{Group} {Label}，未校准"
    };
}

internal static class GamepadLayoutModel
{
    public const int HeatLevelCount = 5;
    public const double CanvasWidth = 680;
    public const double CanvasHeight = 440;
    public const double HotspotSize = 32;

    private sealed record PositionSpec(
        GamepadPhysicalPosition Position,
        string Group,
        string Label,
        double X,
        double Y);

    private static readonly PositionSpec[] XboxPositions =
    [
        new(GamepadPhysicalPosition.LeftTrigger, "扳机键", "LT", 158, 26),
        new(GamepadPhysicalPosition.RightTrigger, "扳机键", "RT", 485, 26),
        new(GamepadPhysicalPosition.LeftShoulder, "肩键", "LB", 158, 52),
        new(GamepadPhysicalPosition.RightShoulder, "肩键", "RB", 485, 52),
        new(GamepadPhysicalPosition.View, "系统键", "View", 283, 148),
        new(GamepadPhysicalPosition.Menu, "系统键", "Menu", 363, 148),
        new(GamepadPhysicalPosition.LeftStickPress, "摇杆按下", "L3", 167, 141),
        new(GamepadPhysicalPosition.DpadUp, "方向键", "↑", 235, 205),
        new(GamepadPhysicalPosition.DpadLeft, "方向键", "←", 206, 233),
        new(GamepadPhysicalPosition.DpadRight, "方向键", "→", 264, 233),
        new(GamepadPhysicalPosition.DpadDown, "方向键", "↓", 235, 261),
        new(GamepadPhysicalPosition.RightStickPress, "摇杆按下", "R3", 398, 233),
        new(GamepadPhysicalPosition.West, "面键", "X", 439, 149),
        new(GamepadPhysicalPosition.North, "面键", "Y", 478, 109),
        new(GamepadPhysicalPosition.East, "面键", "B", 518, 149),
        new(GamepadPhysicalPosition.South, "面键", "A", 478, 189)
    ];

    public static IReadOnlyList<GamepadHeatmapPosition> CreateXboxLayout(
        IReadOnlyDictionary<GamepadPhysicalPosition, long>? pressCounts = null,
        IReadOnlySet<GamepadPhysicalPosition>? supportedPositions = null)
    {
        if (pressCounts is not null && pressCounts.Any(pair => pair.Value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(pressCounts), "按键次数不能为负数。");
        }

        long maximum = pressCounts is null || pressCounts.Count == 0
            ? 0
            : pressCounts.Values.Max();

        return XboxPositions.Select(spec =>
        {
            GamepadPositionSupport support = GetSupport(spec.Position, pressCounts, supportedPositions);
            long? count = support == GamepadPositionSupport.Supported
                ? pressCounts is not null && pressCounts.TryGetValue(spec.Position, out long value) ? value : 0
                : null;

            return new GamepadHeatmapPosition(
                spec.Position,
                spec.Group,
                spec.Label,
                spec.X,
                spec.Y,
                support,
                count,
                count.HasValue ? GetHeatLevel(count.Value, maximum) : null);
        }).ToArray();
    }

    public static int GetHeatLevel(long count, long maximum)
    {
        if (count <= 0 || maximum <= 0) return 0;
        if (count >= maximum) return HeatLevelCount - 1;

        double normalized = Math.Sqrt(count / (double)maximum);
        return Math.Clamp((int)Math.Ceiling(normalized * (HeatLevelCount - 1)), 1, HeatLevelCount - 1);
    }

    private static GamepadPositionSupport GetSupport(
        GamepadPhysicalPosition position,
        IReadOnlyDictionary<GamepadPhysicalPosition, long>? pressCounts,
        IReadOnlySet<GamepadPhysicalPosition>? supportedPositions)
    {
        if (supportedPositions is not null)
        {
            return supportedPositions.Contains(position)
                ? GamepadPositionSupport.Supported
                : GamepadPositionSupport.Unsupported;
        }

        if (pressCounts is not null)
        {
            return pressCounts.ContainsKey(position)
                ? GamepadPositionSupport.Supported
                : GamepadPositionSupport.Unsupported;
        }

        return GamepadPositionSupport.Unknown;
    }
}
