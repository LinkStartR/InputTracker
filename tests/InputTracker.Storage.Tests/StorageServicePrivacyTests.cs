using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace InputTracker.Storage.Tests;

public sealed class StorageServicePrivacyTests
{
    [Fact]
    public void KeyboardTransitionContractHasOnlyPhysicalAndCalibrationFields()
    {
        Dictionary<string, Type> properties = typeof(StorageKeyboardTransition)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(property => property.Name, property => property.PropertyType, StringComparer.Ordinal);

        var expected = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["DeviceId"] = typeof(Guid),
            ["MakeCode"] = typeof(ushort),
            ["IsE0"] = typeof(bool),
            ["IsE1"] = typeof(bool),
            ["IsDown"] = typeof(bool),
            ["MonotonicTimestamp"] = typeof(long),
            ["CalibrationSessionId"] = typeof(Guid)
        };
        Assert.Equal(expected.OrderBy(pair => pair.Key), properties.OrderBy(pair => pair.Key));
        Assert.DoesNotContain(properties.Keys, name => ContainsAny(
            name,
            "Text",
            "Character",
            "VKey",
            "Message",
            "Sequence",
            "Utc",
            "Window",
            "Focus",
            "Process"));
    }

    [Fact]
    public void MouseTransitionContractHasOnlyDeviceCalibrationAndRawPhysicalFields()
    {
        Dictionary<string, Type> properties = typeof(StorageMouseTransition)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(property => property.Name, property => property.PropertyType, StringComparer.Ordinal);
        var expected = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["DeviceId"] = typeof(Guid),
            ["CalibrationSessionId"] = typeof(Guid),
            ["MonotonicTimestamp"] = typeof(long),
            ["DeltaX"] = typeof(int),
            ["DeltaY"] = typeof(int),
            ["MotionKind"] = typeof(byte),
            ["UsesVirtualDesktop"] = typeof(bool),
            ["ButtonChanges"] = typeof(ushort),
            ["VerticalWheelDelta"] = typeof(short),
            ["HorizontalWheelDelta"] = typeof(short)
        };

        Assert.Equal(expected.OrderBy(pair => pair.Key), properties.OrderBy(pair => pair.Key));
        Assert.DoesNotContain(properties.Keys, name => ContainsAny(
            name, "Sequence", "Text", "Character", "Window", "Focus", "Application", "Process", "Interface", "Hardware"));
    }

    [Fact]
    public void MouseIngestionSchemaAndSourcesExcludeContentAndRawIdentityFields()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        IReadOnlyList<string> spoolColumns = database.ReadSchemaColumns()["mouse_spool_segments"];
        IReadOnlyList<string> watermarkColumns = database.ReadSchemaColumns()["mouse_device_watermarks"];
        string schema = string.Join('|', spoolColumns.Concat(watermarkColumns));
        string root = FindRepositoryRoot();
        string source = string.Join('\n', new[] { "MouseIngestion.cs", "MouseSpool.cs" }
            .Select(file => File.ReadAllText(Path.Combine(root, "src", "InputTracker.Storage", file))));
        foreach (string forbidden in new[] { "Text", "Character", "Window", "Focus", "Application", "Process", "InterfacePath", "HardwareIds", "SerialNumber", "ContainerId" })
        {
            Assert.DoesNotContain(forbidden, schema, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(new Regex($@"\b{Regex.Escape(forbidden)}\b", RegexOptions.CultureInvariant), source);
        }
    }

    [Fact]
    public void MouseButtonSchemaContainsOnlyDailyCountsAndSegmentDeduplication()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());

        Assert.Equal(
            ["device_id", "button", "capture_local_date", "time_zone_id", "press_count"],
            database.ReadSchemaColumns()["mouse_button_daily"]);
        Assert.Equal(
            ["spool_segment_id"],
            database.ReadSchemaColumns()["mouse_button_aggregated_segments"]);
        string schema = string.Join('|', database.ReadSchemaColumns()["mouse_button_daily"]);
        foreach (string forbidden in new[]
        {
            "sequence", "timestamp", "button_down", "button_up", "window", "process", "application", "text"
        })
        {
            Assert.DoesNotContain(forbidden, schema, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ProductionServiceDoesNotExposeClockOrFaultInjection()
    {
        Assert.Empty(typeof(StorageService).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        MethodInfo[] methods = typeof(StorageService).GetMethods(
            BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public);

        Assert.DoesNotContain(methods, method => method.GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(IStorageClock)
            || parameter.ParameterType == typeof(IStorageServiceFaultInjector)));
        MethodInfo open = Assert.Single(methods, method => method.Name == nameof(StorageService.Open));
        ParameterInfo parameter = Assert.Single(open.GetParameters());
        Assert.Equal(typeof(StorageLayout), parameter.ParameterType);
    }

    [Fact]
    public void PendingOpenHoldIsNotPersistedAndMetadataContainsOnlyScalarOrdinals()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);
        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 0));

        Assert.Empty(StorageServiceTestSupport.ReadKeyboardRows(layout));
        using SqliteConnection connection = OpenReadOnly(layout);
        using SqliteCommand tables = connection.CreateCommand();
        tables.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table' AND lower(name) LIKE '%keyboard%'
            ORDER BY name;
            """;
        var keyboardTables = new List<string>();
        using (SqliteDataReader tableReader = tables.ExecuteReader())
        {
            while (tableReader.Read())
            {
                keyboardTables.Add(tableReader.GetString(0));
            }
        }

        Assert.Equal(["keyboard_daily"], keyboardTables);

        using SqliteCommand metadata = connection.CreateCommand();
        metadata.CommandText = "SELECT key, value FROM store_metadata ORDER BY key;";
        using SqliteDataReader reader = metadata.ExecuteReader();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            values.Add(reader.GetString(0), reader.GetString(1));
        }

        Assert.Equal(
            [
                "mouse.v3-adoption-complete",
                "storage.service.commit-ordinal",
                "storage.service.keyboard-aggregate-commit-ordinal"
            ],
            values.Keys);
        Assert.Equal("1", values["mouse.v3-adoption-complete"]);
        Assert.All(values
            .Where(pair => pair.Key.StartsWith("storage.service.", StringComparison.Ordinal))
            .Select(pair => pair.Value), value => Assert.True(long.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out _)));
        Assert.Equal("1", values["storage.service.commit-ordinal"]);
        Assert.Equal("0", values["storage.service.keyboard-aggregate-commit-ordinal"]);
    }

    [Fact]
    public void KeyboardIngestionSourcesExcludeForbiddenContentIdentifiers()
    {
        string root = FindRepositoryRoot();
        string source = string.Join(
            '\n',
            new[]
            {
                "StorageService.cs",
                "KeyboardAggregation.cs"
            }.Select(file => File.ReadAllText(Path.Combine(root, "src", "InputTracker.Storage", file))));

        foreach (string identifier in new[] { "Text", "Character", "VKey", "Message", "Sequence", "Window", "Focus", "Process" })
        {
            Assert.DoesNotMatch(
                new Regex($@"\b{Regex.Escape(identifier)}\b", RegexOptions.CultureInvariant),
                source);
        }
    }

    private static SqliteConnection OpenReadOnly(StorageLayout layout)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = layout.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InputTracker.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static bool ContainsAny(string value, params string[] forbidden) =>
        forbidden.Any(part => value.Contains(part, StringComparison.OrdinalIgnoreCase));
}
