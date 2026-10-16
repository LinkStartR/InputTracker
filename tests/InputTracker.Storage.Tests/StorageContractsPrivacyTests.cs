using System.Reflection;

namespace InputTracker.Storage.Tests;

public sealed class StorageContractsPrivacyTests
{
    [Theory]
    [InlineData("MouseRecord")]
    [InlineData("MousePlayback")]
    [InlineData("MouseChunk")]
    [InlineData("MouseSpool")]
    [InlineData("MouseIngestion")]
    [InlineData("MouseTransition")]
    [InlineData("Trajectory")]
    [InlineData("Cursor")]
    [InlineData("MotionKind")]
    [InlineData("DeltaX")]
    [InlineData("DeltaY")]
    [InlineData("Position")]
    [InlineData("Route")]
    [InlineData("VerticalWheel")]
    [InlineData("HorizontalWheel")]
    public void PublicStorageTypesContainNoTrajectorySurface(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(StorageService).Assembly.GetExportedTypes(),
            type => type.Name.Contains(forbidden, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("StreamMouse")]
    [InlineData("OpenMouse")]
    [InlineData("WriteMouseTransition")]
    [InlineData("Playback")]
    [InlineData("Chunk")]
    [InlineData("Spool")]
    [InlineData("Trajectory")]
    [InlineData("Position")]
    [InlineData("Wheel")]
    [InlineData("Motion")]
    public void PublicStorageServiceContainsNoTrajectoryOperation(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(StorageService).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.Name.Contains(forbidden, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("new MouseIngestion")]
    [InlineData("_mouse")]
    [InlineData("WriteMouseTransition(")]
    [InlineData("StreamMouseAsync(")]
    [InlineData("OpenMousePlaybackSnapshotAsync(")]
    [InlineData("MouseSpoolWriter")]
    [InlineData("MouseChunkReader")]
    [InlineData("DeltaX")]
    [InlineData("DeltaY")]
    [InlineData("WheelDelta")]
    public void RuntimeStorageServiceSourceContainsNoTrajectoryPath(string forbidden)
    {
        string root = FindRepositoryRoot();
        string source = string.Join('\n', new[] { "StorageService.cs", "StorageQuery.cs" }
            .Select(file => File.ReadAllText(Path.Combine(root, "src", "InputTracker.Storage", file))));
        Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

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
    public void PublicStorageSurfaceHasNoMouseTrajectoryContractsOrOperations()
    {
        string[] exportedTypeNames = typeof(StorageService).Assembly
            .GetExportedTypes()
            .Select(type => type.Name)
            .ToArray();
        string[] publicServiceMethods = typeof(StorageService)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain(exportedTypeNames, name => ContainsAny(
            name, "MouseRecord", "MouseTransition", "MousePlayback", "MouseChunk", "MouseSpool"));
        Assert.DoesNotContain(publicServiceMethods, name => ContainsAny(
            name, "WriteMouseTransition", "StreamMouse", "OpenMousePlayback"));
        Assert.Contains(nameof(StorageService.WriteMouseButtonTransition), publicServiceMethods);
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

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InputTracker.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
