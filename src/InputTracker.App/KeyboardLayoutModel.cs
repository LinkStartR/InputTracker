namespace InputTracker;

internal readonly record struct KeyboardKeyIdentity(ushort MakeCode, bool IsE0, bool IsE1);

internal enum KeyboardRegion
{
    Main,
    Modifier,
    Navigation,
    Arrow,
    Numpad,
    Function,
    Unknown
}

internal sealed record KeyboardLayoutKey(
    KeyboardKeyIdentity Identity,
    string Label,
    KeyboardRegion Region,
    bool IsKnown,
    double X,
    double Y,
    double Width,
    double Height);

internal sealed class KeyboardLayoutModel
{
    internal const double CanvasWidth = 1180;
    internal const double CanvasHeight = 340;
    private const double KeyWidth = 50;
    private const double KeyHeight = 48;
    private const double Gap = 4;
    private readonly IReadOnlyList<KeyboardLayoutKey> _keys;
    private readonly IReadOnlyDictionary<KeyboardKeyIdentity, KeyboardLayoutKey> _byIdentity;

    private KeyboardLayoutModel(IReadOnlyList<KeyboardLayoutKey> keys)
    {
        _keys = keys;
        _byIdentity = keys.ToDictionary(key => key.Identity);
    }

    public IReadOnlyList<KeyboardLayoutKey> Keys => _keys;

    public KeyboardLayoutKey Resolve(ushort makeCode, bool isE0, bool isE1)
    {
        var identity = new KeyboardKeyIdentity(makeCode, isE0, isE1);
        return _byIdentity.TryGetValue(identity, out KeyboardLayoutKey? key)
            ? key
            : new KeyboardLayoutKey(
                identity,
                $"{(isE0 ? "E0-" : isE1 ? "E1-" : string.Empty)}{makeCode:X2}",
                KeyboardRegion.Unknown,
                IsKnown: false,
                X: 0,
                Y: 0,
                Width: 76,
                Height: KeyHeight);
    }

    public static KeyboardLayoutModel CreateDefault()
    {
        var keys = new List<KeyboardLayoutKey>();

        Add(keys, 0x01, false, false, "Esc", KeyboardRegion.Function, 0, 0);
        ushort[] functionCodes = [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58];
        for (int index = 0; index < functionCodes.Length; index++)
        {
            double groupGap = (index / 4) * 12;
            Add(keys, functionCodes[index], false, false, $"F{index + 1}", KeyboardRegion.Function,
                112 + (index * (KeyWidth + Gap)) + groupGap, 0);
        }

        Add(keys, 0x37, true, false, "PrtSc", KeyboardRegion.Navigation, 790, 0);
        Add(keys, 0x46, false, false, "ScrLk", KeyboardRegion.Navigation, 844, 0);
        Add(keys, 0x45, false, true, "Pause", KeyboardRegion.Navigation, 898, 0);

        AddRow(keys, 0, 60, KeyboardRegion.Main,
            K("`", 0x29), K("1", 0x02), K("2", 0x03), K("3", 0x04), K("4", 0x05),
            K("5", 0x06), K("6", 0x07), K("7", 0x08), K("8", 0x09), K("9", 0x0A),
            K("0", 0x0B), K("-", 0x0C), K("=", 0x0D), K("Backspace", 0x0E, width: 2));
        AddRow(keys, 0, 116, KeyboardRegion.Main,
            K("Tab", 0x0F, width: 1.5), K("Q", 0x10), K("W", 0x11), K("E", 0x12), K("R", 0x13),
            K("T", 0x14), K("Y", 0x15), K("U", 0x16), K("I", 0x17), K("O", 0x18), K("P", 0x19),
            K("[", 0x1A), K("]", 0x1B), K("\\", 0x2B, width: 1.5));
        AddRow(keys, 0, 172, KeyboardRegion.Main,
            K("Caps", 0x3A, width: 1.75), K("A", 0x1E), K("S", 0x1F), K("D", 0x20), K("F", 0x21),
            K("G", 0x22), K("H", 0x23), K("J", 0x24), K("K", 0x25), K("L", 0x26), K(";", 0x27),
            K("'", 0x28), K("Enter", 0x1C, width: 2.25));
        AddRow(keys, 0, 228, KeyboardRegion.Main,
            K("LShift", 0x2A, KeyboardRegion.Modifier, width: 2.25), K("Z", 0x2C), K("X", 0x2D),
            K("C", 0x2E), K("V", 0x2F), K("B", 0x30), K("N", 0x31), K("M", 0x32), K(",", 0x33),
            K(".", 0x34), K("/", 0x35), K("RShift", 0x36, KeyboardRegion.Modifier, width: 2.75));
        AddRow(keys, 0, 284, KeyboardRegion.Modifier,
            K("LCtrl", 0x1D), K("Win", 0x5B, e0: true), K("LAlt", 0x38),
            K("Space", 0x39, KeyboardRegion.Main, width: 6.25), K("AltGr", 0x38, e0: true),
            K("Win", 0x5C, e0: true), K("Menu", 0x5D, e0: true), K("RCtrl", 0x1D, e0: true));

        AddNavigation(keys);
        AddNumpad(keys);
        return new KeyboardLayoutModel(keys);
    }

    private static void AddNavigation(List<KeyboardLayoutKey> keys)
    {
        Add(keys, 0x52, true, false, "Ins", KeyboardRegion.Navigation, 790, 60);
        Add(keys, 0x47, true, false, "Home", KeyboardRegion.Navigation, 844, 60);
        Add(keys, 0x49, true, false, "PgUp", KeyboardRegion.Navigation, 898, 60);
        Add(keys, 0x53, true, false, "Del", KeyboardRegion.Navigation, 790, 116);
        Add(keys, 0x4F, true, false, "End", KeyboardRegion.Navigation, 844, 116);
        Add(keys, 0x51, true, false, "PgDn", KeyboardRegion.Navigation, 898, 116);
        Add(keys, 0x48, true, false, "↑", KeyboardRegion.Arrow, 844, 228);
        Add(keys, 0x4B, true, false, "←", KeyboardRegion.Arrow, 790, 284);
        Add(keys, 0x50, true, false, "↓", KeyboardRegion.Arrow, 844, 284);
        Add(keys, 0x4D, true, false, "→", KeyboardRegion.Arrow, 898, 284);
    }

    private static void AddNumpad(List<KeyboardLayoutKey> keys)
    {
        const double x = 974;
        Add(keys, 0x45, false, false, "Num", KeyboardRegion.Numpad, x, 60);
        Add(keys, 0x35, true, false, "/", KeyboardRegion.Numpad, x + 54, 60);
        Add(keys, 0x37, false, false, "*", KeyboardRegion.Numpad, x + 108, 60);
        Add(keys, 0x4A, false, false, "-", KeyboardRegion.Numpad, x + 162, 60);
        Add(keys, 0x47, false, false, "7", KeyboardRegion.Numpad, x, 116);
        Add(keys, 0x48, false, false, "8", KeyboardRegion.Numpad, x + 54, 116);
        Add(keys, 0x49, false, false, "9", KeyboardRegion.Numpad, x + 108, 116);
        Add(keys, 0x4E, false, false, "+", KeyboardRegion.Numpad, x + 162, 116, height: 104);
        Add(keys, 0x4B, false, false, "4", KeyboardRegion.Numpad, x, 172);
        Add(keys, 0x4C, false, false, "5", KeyboardRegion.Numpad, x + 54, 172);
        Add(keys, 0x4D, false, false, "6", KeyboardRegion.Numpad, x + 108, 172);
        Add(keys, 0x4F, false, false, "1", KeyboardRegion.Numpad, x, 228);
        Add(keys, 0x50, false, false, "2", KeyboardRegion.Numpad, x + 54, 228);
        Add(keys, 0x51, false, false, "3", KeyboardRegion.Numpad, x + 108, 228);
        Add(keys, 0x1C, true, false, "Enter", KeyboardRegion.Numpad, x + 162, 228, height: 104);
        Add(keys, 0x52, false, false, "0", KeyboardRegion.Numpad, x, 284, width: 104);
        Add(keys, 0x53, false, false, ".", KeyboardRegion.Numpad, x + 108, 284);
    }

    private static KeySpec K(
        string label,
        ushort makeCode,
        KeyboardRegion? region = null,
        bool e0 = false,
        bool e1 = false,
        double width = 1) => new(label, makeCode, e0, e1, region, width);

    private static void AddRow(
        List<KeyboardLayoutKey> keys,
        double startX,
        double y,
        KeyboardRegion defaultRegion,
        params KeySpec[] row)
    {
        double x = startX;
        foreach (KeySpec key in row)
        {
            double width = (KeyWidth * key.WidthUnits) + (Gap * Math.Max(0, key.WidthUnits - 1));
            Add(keys, key.MakeCode, key.IsE0, key.IsE1, key.Label, key.Region ?? defaultRegion, x, y, width);
            x += width + Gap;
        }
    }

    private static void Add(
        List<KeyboardLayoutKey> keys,
        ushort makeCode,
        bool isE0,
        bool isE1,
        string label,
        KeyboardRegion region,
        double x,
        double y,
        double width = KeyWidth,
        double height = KeyHeight) =>
        keys.Add(new KeyboardLayoutKey(
            new KeyboardKeyIdentity(makeCode, isE0, isE1), label, region, true,
            x, y, width, height));

    private readonly record struct KeySpec(
        string Label,
        ushort MakeCode,
        bool IsE0,
        bool IsE1,
        KeyboardRegion? Region,
        double WidthUnits);
}

internal static class KeyboardHeatScale
{
    public const int LevelCount = 6;

    public static int GetLevel(long count, long maximum)
    {
        if (count <= 0 || maximum <= 0) return 0;
        if (count >= maximum) return LevelCount - 1;

        double normalized = Math.Sqrt(count / (double)maximum);
        return Math.Clamp((int)Math.Ceiling(normalized * (LevelCount - 1)), 1, LevelCount - 1);
    }
}

internal sealed record KeyboardHeatmapKey(
    KeyboardKeyIdentity Identity,
    string Label,
    KeyboardRegion Region,
    bool IsKnown,
    double X,
    double Y,
    double Width,
    double Height,
    long PressCount,
    long HoldDurationMicroseconds,
    int HeatLevel)
{
    public string CountText => PressCount == 0 ? "0" : PressCount.ToString("N0");
    public string AccessibleLabel => $"{Label}，{PressCount:N0} 次";
}

internal sealed record KeyboardHeatmapPresentation(
    IReadOnlyList<KeyboardHeatmapKey> PhysicalKeys,
    IReadOnlyList<KeyboardHeatmapKey> OtherKeys,
    long MaximumPressCount)
{
    public static KeyboardHeatmapPresentation Create(
        KeyboardLayoutModel layout,
        IReadOnlyList<HeatmapCell> cells)
    {
        var totals = cells
            .GroupBy(cell => new KeyboardKeyIdentity(cell.MakeCode, cell.IsE0, cell.IsE1))
            .ToDictionary(
                group => group.Key,
                group => (PressCount: group.Sum(cell => cell.PressCount),
                    HoldDuration: group.Sum(cell => cell.HoldDurationMicroseconds)));
        long maximum = totals.Count == 0 ? 0 : totals.Values.Max(value => value.PressCount);

        KeyboardHeatmapKey ToHeatmapKey(KeyboardLayoutKey key)
        {
            (long pressCount, long holdDuration) = totals.TryGetValue(key.Identity, out var aggregate)
                ? aggregate
                : (0, 0);
            return new KeyboardHeatmapKey(
                key.Identity, key.Label, key.Region, key.IsKnown,
                key.X, key.Y, key.Width, key.Height,
                pressCount, holdDuration, KeyboardHeatScale.GetLevel(pressCount, maximum));
        }

        KeyboardHeatmapKey[] physical = layout.Keys.Select(ToHeatmapKey).ToArray();
        var known = layout.Keys.Select(key => key.Identity).ToHashSet();
        KeyboardHeatmapKey[] other = totals
            .Where(pair => !known.Contains(pair.Key))
            .OrderBy(pair => pair.Key.MakeCode)
            .ThenBy(pair => pair.Key.IsE0)
            .ThenBy(pair => pair.Key.IsE1)
            .Select(pair => ToHeatmapKey(layout.Resolve(
                pair.Key.MakeCode, pair.Key.IsE0, pair.Key.IsE1)))
            .ToArray();

        return new KeyboardHeatmapPresentation(physical, other, maximum);
    }
}
