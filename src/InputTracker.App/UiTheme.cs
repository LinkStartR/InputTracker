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
    string AccentText,
    IReadOnlyList<string> HeatFills,
    IReadOnlyList<string> HeatTexts);

internal static class UiThemeCatalog
{
    public static UiThemePalette Light { get; } = new(
        "#FFF8FAFC", "#FFFFFFFF", "#FFF1F5F9", "#FF0F172A", "#FF475569",
        "#FFE2E8F0", "#FF4F46E5", "#FFEEF2FF", "#FFFFFFFF", "#FFFFFFFF",
        ["#FFE2E8F0", "#FFDBEAFE", "#FF93C5FD", "#FF3B82F6", "#FF1D4ED8", "#FF172554"],
        ["#FF0F172A", "#FF0F172A", "#FF0F172A", "#FF0F172A", "#FFFFFFFF", "#FFFFFFFF"]);

    public static UiThemePalette Dark { get; } = new(
        "#FF0F172A", "#FF111827", "#FF1E293B", "#FFF8FAFC", "#FFCBD5E1",
        "#FF334155", "#FF818CF8", "#FF1E1B4B", "#FF111827", "#FF0F172A",
        ["#FF1E293B", "#FF1E3A5F", "#FF1D4ED8", "#FF2563EB", "#FF60A5FA", "#FFBFDBFE"],
        ["#FFF8FAFC", "#FFF8FAFC", "#FFFFFFFF", "#FFFFFFFF", "#FF0F172A", "#FF0F172A"]);
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
