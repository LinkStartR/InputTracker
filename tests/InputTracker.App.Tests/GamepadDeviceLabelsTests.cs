using InputTracker.Storage;

namespace InputTracker.App.Tests;

public sealed class GamepadDeviceLabelsTests
{
    [Fact]
    public void LabelsUseAliasOrStableCreationOrderWithoutFormattingDeviceId()
    {
        Guid later = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
        Guid earlier = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid keyboard = Guid.NewGuid();
        StorageDeviceInfo[] devices =
        [
            Device(later, StorageDeviceKind.Gamepad, 20, null),
            Device(keyboard, StorageDeviceKind.Keyboard, 1, null),
            Device(earlier, StorageDeviceKind.Gamepad, 10, "Desk controller")
        ];

        IReadOnlyDictionary<Guid, string> labels = GamepadDeviceLabels.Create(devices);

        Assert.Equal("Desk controller", labels[earlier]);
        Assert.Equal("手柄 2", labels[later]);
        Assert.DoesNotContain(keyboard, labels.Keys);
        Assert.DoesNotContain(later.ToString("D"), string.Join("|", labels.Values), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EqualCreationTimesUseStableDeviceOrder()
    {
        Guid first = Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid second = Guid.Parse("20000000-0000-0000-0000-000000000001");

        IReadOnlyDictionary<Guid, string> labels = GamepadDeviceLabels.Create(
        [
            Device(second, StorageDeviceKind.Gamepad, 5, null),
            Device(first, StorageDeviceKind.Gamepad, 5, null)
        ]);

        Assert.Equal("手柄 1", labels[first]);
        Assert.Equal("手柄 2", labels[second]);
    }

    private static StorageDeviceInfo Device(
        Guid id, StorageDeviceKind kind, long created, string? alias) => new(
            id, kind, StorageDeviceIdentityConfidence.ReconnectableHardware,
            StorageDeviceIdentityAmbiguity.None, alias, created, created);
}
