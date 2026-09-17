using System.Globalization;
using System.Xml.Linq;

namespace InputTracker.App.Tests;

public sealed class UiPresentationTests
{
    [Theory]
    [InlineData("MainWindow.xaml", "MouseCanvasGrid")]
    [InlineData("MainWindow.xaml", "MousePlaybackPlayButton")]
    [InlineData("MainWindow.xaml", "MousePlaybackSpeedCombo")]
    [InlineData("MainWindow.xaml", "MouseRouteSummaryText")]
    [InlineData("MainWindow.xaml", "PreviewMouseDeletionButton")]
    [InlineData("MainWindow.xaml", "鼠标轨迹")]
    [InlineData("MainWindow.xaml", "移动路线")]
    [InlineData("MainWindow.xaml", "轨迹回放")]
    [InlineData("MainWindow.xaml.cs", "DrawMouseTrajectory")]
    [InlineData("MainWindow.xaml.cs", "ProjectMousePoint")]
    [InlineData("MainWindow.xaml.cs", "MousePlaybackTimer_Tick")]
    [InlineData("MainWindow.xaml.cs", "FormatMouseRoute")]
    [InlineData("App.xaml.cs", "MousePlaybackController")]
    [InlineData("DashboardViewModel.cs", "MousePoints")]
    [InlineData("DashboardViewModel.cs", "StreamMouseAsync")]
    [InlineData("AppStorageCoordinator.cs", "StorageMouseTransition")]
    public void ProductUiAndRuntimeContainNoTrajectoryFeatureToken(string fileName, string forbidden)
    {
        string path = Path.Combine(FindRepositoryRoot(), "src", "InputTracker.App", fileName);
        Assert.DoesNotContain(forbidden, File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void MousePageShowsButtonCountsWithoutTrajectoryControls()
    {
        string windowXaml = LoadXaml("MainWindow.xaml").ToString(SaveOptions.DisableFormatting);

        Assert.Contains("Header=\"鼠标按键\"", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MouseCanvasGrid", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MousePlaybackPlayButton", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("鼠标轨迹", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("移动路线", windowXaml, StringComparison.Ordinal);
    }

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void NavigationShellExposesEveryRequiredPageWhileCollectionControlsRemainGlobal()
    {
        XDocument document = LoadXaml("MainWindow.xaml");
        XElement root = Assert.IsType<XElement>(document.Root);

        XElement navigation = FindNamedElement(root, "NavigationList");
        string[] destinations = navigation.DescendantsAndSelf()
            .Select(element => (string?)element.Attribute("Tag"))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Cast<string>()
            .ToArray();

        Assert.Contains("Overview", destinations);
        Assert.Contains("KeyboardHeatmap", destinations);
        Assert.Contains("MouseButtons", destinations);
        Assert.DoesNotContain("MousePlayback", destinations);
        Assert.Contains("DataTools", destinations);
        Assert.Contains("Settings", destinations);
        Assert.Contains("Privacy", destinations);

        XElement globalBar = FindNamedElement(root, "GlobalCollectionBar");
        XElement pageContent = FindNamedElement(root, "PageContentHost");
        Assert.DoesNotContain(globalBar, pageContent.DescendantsAndSelf());
        Assert.DoesNotContain(pageContent, globalBar.AncestorsAndSelf());
        Assert.Contains(globalBar.DescendantsAndSelf(), element => HasName(element, "StatusLabel"));
        Assert.Contains(globalBar.DescendantsAndSelf(), element => HasName(element, "PauseButton"));
        Assert.Contains(globalBar.DescendantsAndSelf(), element => HasName(element, "ResumeButton"));
    }

    [Fact]
    public void ThemeResourcesUseDynamicIndirectionAndBothPalettesMeetWcagContrast()
    {
        XDocument appDocument = LoadXaml("App.xaml");
        XElement appRoot = Assert.IsType<XElement>(appDocument.Root);
        string appXaml = appDocument.ToString(SaveOptions.DisableFormatting);
        string windowXaml = LoadXaml("MainWindow.xaml").ToString(SaveOptions.DisableFormatting);

        Assert.Contains("{DynamicResource", appXaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource WindowBackgroundBrush}", windowXaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource PrimaryTextBrush}", windowXaml, StringComparison.Ordinal);

        RgbColor lightBackground = ReadColor(appRoot, "LightWindowBackgroundColor");
        RgbColor lightText = ReadColor(appRoot, "LightPrimaryTextColor");
        RgbColor darkBackground = ReadColor(appRoot, "DarkWindowBackgroundColor");
        RgbColor darkText = ReadColor(appRoot, "DarkPrimaryTextColor");

        Assert.True(
            ContrastRatio(lightText, lightBackground) >= 4.5,
            $"Light theme ordinary text contrast was {ContrastRatio(lightText, lightBackground):F2}:1.");
        Assert.True(
            ContrastRatio(darkText, darkBackground) >= 4.5,
            $"Dark theme ordinary text contrast was {ContrastRatio(darkText, darkBackground):F2}:1.");
    }

    [Fact]
    public void ComboBoxTemplateUsesThemeResourcesForClosedAndPopupSurfaces()
    {
        string windowXaml = LoadXaml("MainWindow.xaml").ToString(SaveOptions.DisableFormatting);

        Assert.Contains("TargetType=\"ComboBox\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource CardBackgroundBrush}", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{DynamicResource PrimaryTextBrush}\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("TextElement.Foreground=\"{TemplateBinding Foreground}\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("ContentTemplateSelector=\"{TemplateBinding ItemTemplateSelector}\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("ContentTemplateSelector=\"{TemplateBinding ContentTemplateSelector}\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PART_Popup\"", windowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptionsStayReadableWithTheApplicationFont()
    {
        string windowXaml = LoadXaml("MainWindow.xaml").ToString(SaveOptions.DisableFormatting);

        Assert.DoesNotContain("FontSize=\"10\"", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FontSize=\"11\"", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Property=\"FontSize\" Value=\"11\"", windowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualShellUsesDedicatedNavigationPaletteAndRestrainedElevation()
    {
        string appXaml = LoadXaml("App.xaml").ToString(SaveOptions.DisableFormatting);
        string windowXaml = LoadXaml("MainWindow.xaml").ToString(SaveOptions.DisableFormatting);

        Assert.Contains("x:Key=\"SidebarTextBrush\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SidebarMutedTextBrush\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SidebarSelectionBrush\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"CardShadowEffect\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NavigationGlyph\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("ContentSource=\"SelectedContent\" TextElement.Foreground=\"{DynamicResource PrimaryTextBrush}\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"鼠标按键\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalCollectionBar\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DirectoryText\"", windowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void LightThemeUsesALightReadableNavigationSurface()
    {
        UiThemePalette palette = UiThemeCatalog.Light;
        RgbColor background = ParseColor(palette.SidebarBackground);
        RgbColor normalText = ParseColor(palette.SidebarMutedText);
        RgbColor selectedBackground = ParseColor(palette.SidebarSelection);
        RgbColor selectedText = ParseColor(palette.SidebarText);

        Assert.True(RelativeLuminance(background) >= 0.80, "Light navigation background must remain visibly light.");
        Assert.True(ContrastRatio(normalText, background) >= 4.5);
        Assert.True(ContrastRatio(selectedText, selectedBackground) >= 4.5);
    }

    [Fact]
    public void ApplicationTypographyPrefersMicrosoftYaHeiUiWithSystemFallbacks()
    {
        string appXaml = LoadXaml("App.xaml").ToString(SaveOptions.DisableFormatting);
        string windowXaml = LoadXaml("MainWindow.xaml").ToString(SaveOptions.DisableFormatting);

        Assert.Contains("x:Key=\"AppTextFontFamily\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Microsoft YaHei UI", appXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LXGW WenKai", appXaml, StringComparison.Ordinal);
        Assert.Contains("Segoe UI Variable Text", appXaml, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"{DynamicResource AppTextFontFamily}\"", windowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalPageDescriptionsAvoidImplementationJargon()
    {
        string windowXaml = LoadXaml("MainWindow.xaml").ToString(SaveOptions.DisableFormatting);

        Assert.Contains("键盘按了多少次、鼠标点了多少次，一眼就能看到。", windowXaml, StringComparison.Ordinal);
        Assert.Contains("颜色越深，说明这个按键用得越多。", windowXaml, StringComparison.Ordinal);
        Assert.Contains("改名只方便你辨认，已有记录不会变化。", windowXaml, StringComparison.Ordinal);
        Assert.Contains("删除前只能查看范围，不会直接动数据。", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Storage", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("固定快照", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("完整枚举后的有界显示", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("未识别的扫描码", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("统计归属", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("实体按键", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("敏感操作", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("影响范围", windowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultKeyboardLayoutCoversMainModifiersNavigationArrowsAndNumpad()
    {
        KeyboardLayoutModel layout = KeyboardLayoutModel.CreateDefault();

        Assert.Equal(KeyboardRegion.Main, layout.Resolve(0x1E, isE0: false, isE1: false).Region); // A
        Assert.Equal(KeyboardRegion.Main, layout.Resolve(0x39, isE0: false, isE1: false).Region); // Space
        Assert.Equal(KeyboardRegion.Modifier, layout.Resolve(0x1D, isE0: false, isE1: false).Region); // Left Ctrl
        Assert.Equal(KeyboardRegion.Modifier, layout.Resolve(0x1D, isE0: true, isE1: false).Region); // Right Ctrl
        Assert.Equal(KeyboardRegion.Navigation, layout.Resolve(0x52, isE0: true, isE1: false).Region); // Insert
        Assert.Equal(KeyboardRegion.Arrow, layout.Resolve(0x48, isE0: true, isE1: false).Region); // Up
        Assert.Equal(KeyboardRegion.Numpad, layout.Resolve(0x48, isE0: false, isE1: false).Region); // Numpad 8
    }

    [Fact]
    public void KeyboardIdentityKeepsE0E1DistinctAndUnknownKeysVisible()
    {
        KeyboardLayoutModel layout = KeyboardLayoutModel.CreateDefault();

        KeyboardLayoutKey plain = layout.Resolve(0x1D, isE0: false, isE1: false);
        KeyboardLayoutKey e0 = layout.Resolve(0x1D, isE0: true, isE1: false);
        KeyboardLayoutKey e1 = layout.Resolve(0x1D, isE0: false, isE1: true);
        KeyboardLayoutKey unknown = layout.Resolve(0x7F, isE0: true, isE1: false);

        Assert.NotEqual(plain.Identity, e0.Identity);
        Assert.NotEqual(plain.Identity, e1.Identity);
        Assert.NotEqual(e0.Identity, e1.Identity);
        Assert.False(unknown.IsKnown);
        Assert.Equal((ushort)0x7F, unknown.Identity.MakeCode);
        Assert.True(unknown.Identity.IsE0);
        Assert.False(unknown.Identity.IsE1);
        Assert.False(string.IsNullOrWhiteSpace(unknown.Label));
    }

    [Fact]
    public void HeatScaleHasAtLeastFiveMonotonicLevelsAndSeparatesZeroFromActivity()
    {
        Assert.True(KeyboardHeatScale.LevelCount >= 5);

        int[] levels = [
            KeyboardHeatScale.GetLevel(0, 100),
            KeyboardHeatScale.GetLevel(1, 100),
            KeyboardHeatScale.GetLevel(10, 100),
            KeyboardHeatScale.GetLevel(50, 100),
            KeyboardHeatScale.GetLevel(100, 100)
        ];

        Assert.Equal(0, levels[0]);
        Assert.True(levels[1] > levels[0]);
        Assert.Equal(levels.Order().ToArray(), levels);
        Assert.All(levels, level => Assert.InRange(level, 0, KeyboardHeatScale.LevelCount - 1));
        Assert.Equal(KeyboardHeatScale.LevelCount - 1, levels[^1]);
    }

    [Fact]
    public void HeatmapPresentationPreservesKnownAndUnknownPhysicalKeyAggregates()
    {
        KeyboardLayoutModel layout = KeyboardLayoutModel.CreateDefault();
        HeatmapCell knownAggregate = new(
            MakeCode: 0x1E,
            IsE0: false,
            IsE1: false,
            PressCount: 17,
            HoldDurationMicroseconds: 280_000);
        HeatmapCell unknownAggregate = new(
            MakeCode: 0x7F,
            IsE0: true,
            IsE1: false,
            PressCount: 3,
            HoldDurationMicroseconds: 45_000);

        KeyboardHeatmapPresentation presentation = KeyboardHeatmapPresentation.Create(
            layout,
            [knownAggregate, unknownAggregate]);

        KeyboardHeatmapKey known = Assert.Single(
            presentation.PhysicalKeys,
            key => key.Identity == new KeyboardKeyIdentity(0x1E, IsE0: false, IsE1: false));
        Assert.True(known.IsKnown);
        Assert.Equal(17, known.PressCount);
        Assert.Equal(280_000, known.HoldDurationMicroseconds);
        Assert.True(known.HeatLevel > 0);

        KeyboardHeatmapKey unknown = Assert.Single(presentation.OtherKeys);
        Assert.False(unknown.IsKnown);
        Assert.Equal(new KeyboardKeyIdentity(0x7F, IsE0: true, IsE1: false), unknown.Identity);
        Assert.Equal(3, unknown.PressCount);
        Assert.Equal(45_000, unknown.HoldDurationMicroseconds);
        Assert.True(unknown.HeatLevel > 0);
    }

    [Fact]
    public void EveryHeatmapPaletteLevelKeepsLabelsReadableInLightAndDarkThemes()
    {
        foreach ((string name, UiThemePalette palette) in new[]
        {
            ("Light", UiThemeCatalog.Light),
            ("Dark", UiThemeCatalog.Dark)
        })
        {
            Assert.Equal(KeyboardHeatScale.LevelCount, palette.HeatFills.Count);
            Assert.Equal(KeyboardHeatScale.LevelCount, palette.HeatTexts.Count);

            for (int level = 0; level < KeyboardHeatScale.LevelCount; level++)
            {
                RgbColor fill = ParseColor(palette.HeatFills[level]);
                RgbColor text = ParseColor(palette.HeatTexts[level]);
                double ratio = ContrastRatio(text, fill);

                Assert.True(
                    ratio >= 4.5,
                    $"{name} heat level {level} label contrast was {ratio:F2}:1.");
            }
        }
    }

    private static XDocument LoadXaml(string fileName) =>
        XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "InputTracker.App", fileName));

    private static XElement FindNamedElement(XElement root, string name) =>
        root.DescendantsAndSelf().Single(element => HasName(element, name));

    private static bool HasName(XElement element, string name) =>
        string.Equals((string?)element.Attribute(Xaml + "Name"), name, StringComparison.Ordinal);

    private static RgbColor ReadColor(XElement root, string key)
    {
        XElement element = root.Descendants()
            .Single(candidate => string.Equals((string?)candidate.Attribute(Xaml + "Key"), key, StringComparison.Ordinal));
        Assert.Equal(Presentation + "Color", element.Name);
        return ParseColor(element.Value);
    }

    private static RgbColor ParseColor(string source)
    {
        string value = source.Trim().TrimStart('#');
        if (value.Length == 8)
        {
            value = value[2..];
        }

        Assert.Equal(6, value.Length);
        return new RgbColor(
            byte.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static double ContrastRatio(RgbColor foreground, RgbColor background)
    {
        double lighter = Math.Max(RelativeLuminance(foreground), RelativeLuminance(background));
        double darker = Math.Min(RelativeLuminance(foreground), RelativeLuminance(background));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(RgbColor color) =>
        (0.2126 * Linearize(color.Red))
        + (0.7152 * Linearize(color.Green))
        + (0.0722 * Linearize(color.Blue));

    private static double Linearize(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InputTracker.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate InputTracker.sln from the test output directory.");
    }

    private readonly record struct RgbColor(byte Red, byte Green, byte Blue);
}
