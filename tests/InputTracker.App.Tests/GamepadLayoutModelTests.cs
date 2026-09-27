using InputTracker;

namespace InputTracker.App.Tests;

public sealed class GamepadLayoutModelTests
{
    [Fact]
    public void CreateXboxLayout_ContainsOnlyPhysicalButtonPositions()
    {
        IReadOnlyList<GamepadHeatmapPosition> positions = GamepadLayoutModel.CreateXboxLayout();

        Assert.Equal(16, positions.Count);
        Assert.Equal(positions.Count, positions.Select(position => position.Position).Distinct().Count());
        Assert.Contains(positions, position => position.Position == GamepadPhysicalPosition.LeftTrigger);
        Assert.Contains(positions, position => position.Position == GamepadPhysicalPosition.RightTrigger);
        Assert.DoesNotContain(positions, position => position.Label is "LS" or "RS" or "X/Y Axis");
    }

    [Fact]
    public void CreateXboxLayout_DistinguishesUnknownUnsupportedAndZeroCount()
    {
        IReadOnlyList<GamepadHeatmapPosition> unknown = GamepadLayoutModel.CreateXboxLayout();
        var supported = new HashSet<GamepadPhysicalPosition>
        {
            GamepadPhysicalPosition.South,
            GamepadPhysicalPosition.East
        };
        IReadOnlyList<GamepadHeatmapPosition> calibrated = GamepadLayoutModel.CreateXboxLayout(
            new Dictionary<GamepadPhysicalPosition, long>
            {
                [GamepadPhysicalPosition.South] = 0,
                [GamepadPhysicalPosition.East] = 12
            },
            supported);

        GamepadHeatmapPosition unknownSouth = Assert.Single(unknown, position => position.Position == GamepadPhysicalPosition.South);
        GamepadHeatmapPosition zeroSouth = Assert.Single(calibrated, position => position.Position == GamepadPhysicalPosition.South);
        GamepadHeatmapPosition unsupportedWest = Assert.Single(calibrated, position => position.Position == GamepadPhysicalPosition.West);

        Assert.Equal(GamepadPositionSupport.Unknown, unknownSouth.Support);
        Assert.Equal("未校准", unknownSouth.CountText);
        Assert.Equal(GamepadPositionSupport.Supported, zeroSouth.Support);
        Assert.Equal(0, zeroSouth.PressCount);
        Assert.Equal(0, zeroSouth.HeatLevel);
        Assert.Equal("0 次", zeroSouth.CountText);
        Assert.Equal(GamepadPositionSupport.Unsupported, unsupportedWest.Support);
        Assert.Equal("不支持", unsupportedWest.CountText);
        Assert.Equal(4, Assert.Single(calibrated, position => position.Position == GamepadPhysicalPosition.East).HeatLevel);
    }

    [Fact]
    public void CreateXboxLayout_HeatSpotsFitControllerCanvas()
    {
        IReadOnlyList<GamepadHeatmapPosition> positions = GamepadLayoutModel.CreateXboxLayout();

        Assert.All(positions, position =>
        {
            Assert.InRange(position.X, 0, GamepadLayoutModel.CanvasWidth - GamepadLayoutModel.HotspotSize);
            Assert.InRange(position.Y, 0, GamepadLayoutModel.CanvasHeight - GamepadLayoutModel.HotspotSize);
        });

        GamepadHeatmapPosition south = Assert.Single(positions, position => position.Position == GamepadPhysicalPosition.South);
        Assert.Equal(478, south.X);
        Assert.Equal(189, south.Y);
    }

    [Fact]
    public void CreateXboxLayout_ShortensSystemHotspotLabelsWithoutChangingAccessibleNames()
    {
        IReadOnlyList<GamepadHeatmapPosition> positions = GamepadLayoutModel.CreateXboxLayout();
        GamepadHeatmapPosition view = Assert.Single(positions, position => position.Position == GamepadPhysicalPosition.View);
        GamepadHeatmapPosition menu = Assert.Single(positions, position => position.Position == GamepadPhysicalPosition.Menu);

        Assert.Equal("V", view.DisplayLabel);
        Assert.Equal("View", view.Label);
        Assert.Contains("View", view.AccessibleLabel, StringComparison.Ordinal);
        Assert.Equal("M", menu.DisplayLabel);
        Assert.Equal("Menu", menu.Label);
        Assert.Contains("Menu", menu.AccessibleLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void GetHeatLevel_OrdersNonZeroCountsAndClampsAtMaximum()
    {
        int[] levels = [
            GamepadLayoutModel.GetHeatLevel(0, 100),
            GamepadLayoutModel.GetHeatLevel(1, 100),
            GamepadLayoutModel.GetHeatLevel(25, 100),
            GamepadLayoutModel.GetHeatLevel(100, 100),
            GamepadLayoutModel.GetHeatLevel(500, 100)
        ];

        Assert.Equal(levels.Order(), levels);
        Assert.Equal(GamepadLayoutModel.HeatLevelCount - 1, levels[^1]);
    }

    [Fact]
    public void CreateXboxLayout_RejectsNegativeCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GamepadLayoutModel.CreateXboxLayout(
            new Dictionary<GamepadPhysicalPosition, long> { [GamepadPhysicalPosition.South] = -1 }));
    }
}
