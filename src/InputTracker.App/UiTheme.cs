using System.Windows;
using System.Windows.Media;

namespace InputTracker;

internal enum UiThemeKind
{
    Light,
    Dark
}

internal sealed record UiThemePalette(
    string WindowBackground,
    string CardBackground,
    string SubtleBackground,
    string PrimaryText,
    string SecondaryText,
    string Border,
    string Accent,
    string AccentSoft,
    string SidebarBackground,
    string SidebarText,
    string SidebarMutedText,
    string SidebarSelection,
    string AccentText,
    IReadOnlyList<string> HeatFills,
    IReadOnlyList<string> HeatTexts);

internal static class UiThemeCatalog
{
    public static UiThemePalette Light { get; } = new(
        "#FFF4F8F7", "#FFFFFFFF", "#FFEEF6F3", "#FF18332F", "#FF5B7068",
        "#FFDCE8E3", "#FF087F75", "#FFE4F4EF", "#FFFFFFFF", "#FF075F57",
        "#FF5B7068", "#FFE4F4EF", "#FFFFFFFF",
        ["#FFE3EDEA", "#FFCFE6E0", "#FF8FC9BE", "#FF37AC9C", "#FF146B5F", "#FF0A3A34"],
        ["#FF0F172A", "#FF0F172A", "#FF0F172A", "#FF0F172A", "#FFFFFFFF", "#FFFFFFFF"]);

    public static UiThemePalette Dark { get; } = new(
        "#FF101C1A", "#FF182824", "#FF1C3029", "#FFEAF4EF", "#FFADC2B8",
        "#FF2C433B", "#FF5BD0B5", "#FF203D34", "#FF0B1614", "#FFF2FBF7",
        "#FF9FB5AC", "#FF1D3A33", "#FF08201B",
        ["#FF1F3A35", "#FF25544A", "#FF2A7263", "#FF43A08C", "#FF5BD0B5", "#FFA8EDD9"],
        ["#FFF8FAFC", "#FFF8FAFC", "#FFF8FAFC", "#FF0F172A", "#FF0F172A", "#FF0F172A"]);
}

internal static class UiThemeController
{
    public static void Apply(ResourceDictionary resources, UiThemeKind theme)
    {
        ArgumentNullException.ThrowIfNull(resources);
        UiThemePalette palette = theme == UiThemeKind.Dark ? UiThemeCatalog.Dark : UiThemeCatalog.Light;

        SetColor(resources, "WindowBackgroundColor", palette.WindowBackground);
        SetColor(resources, "CardBackgroundColor", palette.CardBackground);
        SetColor(resources, "SubtleBackgroundColor", palette.SubtleBackground);
        SetColor(resources, "PrimaryTextColor", palette.PrimaryText);
        SetColor(resources, "SecondaryTextColor", palette.SecondaryText);
        SetColor(resources, "BorderColor", palette.Border);
        SetColor(resources, "AccentColor", palette.Accent);
        SetColor(resources, "AccentSoftColor", palette.AccentSoft);
        SetColor(resources, "SidebarBackgroundColor", palette.SidebarBackground);
        SetColor(resources, "SidebarTextColor", palette.SidebarText);
        SetColor(resources, "SidebarMutedTextColor", palette.SidebarMutedText);
        SetColor(resources, "SidebarSelectionColor", palette.SidebarSelection);
        SetColor(resources, "AccentTextColor", palette.AccentText);

        SetBrush(resources, "WindowBackgroundBrush", palette.WindowBackground);
        SetBrush(resources, "CardBackgroundBrush", palette.CardBackground);
        SetBrush(resources, "SubtleBackgroundBrush", palette.SubtleBackground);
        SetBrush(resources, "PrimaryTextBrush", palette.PrimaryText);
        SetBrush(resources, "SecondaryTextBrush", palette.SecondaryText);
        SetBrush(resources, "BorderBrush", palette.Border);
        SetBrush(resources, "AccentBrush", palette.Accent);
        SetBrush(resources, "AccentSoftBrush", palette.AccentSoft);
        SetBrush(resources, "SidebarBackgroundBrush", palette.SidebarBackground);
        SetBrush(resources, "SidebarTextBrush", palette.SidebarText);
        SetBrush(resources, "SidebarMutedTextBrush", palette.SidebarMutedText);
        SetBrush(resources, "SidebarSelectionBrush", palette.SidebarSelection);
        SetBrush(resources, "AccentTextBrush", palette.AccentText);

        for (int level = 0; level < KeyboardHeatScale.LevelCount; level++)
        {
            SetBrush(resources, $"HeatmapLevel{level}Brush", palette.HeatFills[level]);
            SetBrush(resources, $"HeatmapLevel{level}TextBrush", palette.HeatTexts[level]);
        }
    }

    private static void SetColor(ResourceDictionary resources, string key, string value) =>
        resources[key] = (System.Windows.Media.Color)
            System.Windows.Media.ColorConverter.ConvertFromString(value)!;

    private static void SetBrush(ResourceDictionary resources, string key, string value)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)
            System.Windows.Media.ColorConverter.ConvertFromString(value)!);
        brush.Freeze();
        resources[key] = brush;
    }
}
