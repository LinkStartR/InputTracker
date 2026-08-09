namespace InputTracker.Storage.Tests;

public sealed class StorageContractsPrivacyTests
{
    [Fact]
    public void StorageProductDoesNotReferenceInputProduct()
    {
        string[] references = typeof(StorageDeviceObservation).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("InputTracker.Input", references, StringComparer.Ordinal);
    }

    [Fact]
    public void KeyboardAggregateContractContainsOnlyPhysicalDailyAggregateFields()
    {
        string[] properties = typeof(StorageKeyboardAggregate)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "CaptureLocalDate", "DeviceId", "HoldDurationMicroseconds", "IsE0", "IsE1",
                "MakeCode", "PressCount", "TimeZoneId"
            ],
            properties);
        Assert.DoesNotContain(properties, property =>
            ContainsAny(property, "Text", "Character", "VKey", "Message", "Sequence", "Timestamp", "Window", "Process"));
    }

    [Fact]
    public void MouseRecordContractPreservesEveryCatalogedRawFieldAndMappedUtc()
    {
        string[] properties = typeof(StorageMouseRecord)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "ButtonChanges", "DeltaX", "DeltaY", "DeviceId", "HorizontalWheelDelta", "MotionKind",
                "Sequence", "UsesVirtualDesktop", "UtcMicroseconds", "VerticalWheelDelta"
            ],
            properties);
        Assert.DoesNotContain(properties, property => ContainsAny(property, "Window", "Process", "Sample"));
    }

    [Fact]
    public void UtcRangeIsStrictlyHalfOpenAndNonEmpty()
    {
        Assert.Equal(new StorageUtcRange(10, 20), new StorageUtcRange(10, 20).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StorageUtcRange(10, 10).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StorageUtcRange(20, 10).Validate());
    }

    [Fact]
    public void SchemaV3ExposesMakeCodeAndPrefixesAsGeneratedAggregateColumns()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        using Microsoft.Data.Sqlite.SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = "PRAGMA table_xinfo(keyboard_daily);";
        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("make_code", columns);
        Assert.Contains("is_e0", columns);
        Assert.Contains("is_e1", columns);
    }

    private static bool ContainsAny(string value, params string[] forbidden) =>
        forbidden.Any(part => value.Contains(part, StringComparison.OrdinalIgnoreCase));
}
