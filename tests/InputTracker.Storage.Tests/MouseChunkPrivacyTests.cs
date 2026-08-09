using System.Reflection;
using System.Text.RegularExpressions;

namespace InputTracker.Storage.Tests;

public sealed class MouseChunkPrivacyTests
{
    [Fact]
    public void PublicPersistentContractsMatchExactFieldAndTypeAllowlists()
    {
        AssertProperties<MouseChunkHeader>(new Dictionary<string, Type>
        {
            ["ChunkId"] = typeof(Guid),
            ["DeviceId"] = typeof(Guid),
            ["CalibrationSegmentId"] = typeof(long),
            ["FirstSequence"] = typeof(long),
            ["LastSequence"] = typeof(long),
            ["FirstMonotonicTimestamp"] = typeof(long),
            ["LastMonotonicTimestamp"] = typeof(long),
            ["EventCount"] = typeof(long),
            ["CanonicalLength"] = typeof(long),
            ["CompressedLength"] = typeof(long)
        });
        AssertProperties<MouseSpoolRecord>(new Dictionary<string, Type>
        {
            ["Sequence"] = typeof(long),
            ["MonotonicTimestamp"] = typeof(long),
            ["DeltaX"] = typeof(int),
            ["DeltaY"] = typeof(int),
            ["MotionKind"] = typeof(byte),
            ["UsesVirtualDesktop"] = typeof(bool),
            ["ButtonChanges"] = typeof(ushort),
            ["VerticalWheelDelta"] = typeof(short),
            ["HorizontalWheelDelta"] = typeof(short)
        });
        AssertProperties<MouseChunkCatalogEntry>(new Dictionary<string, Type>
        {
            ["ChunkId"] = typeof(Guid),
            ["DeviceId"] = typeof(Guid),
            ["CalibrationSegmentId"] = typeof(long),
            ["RelativePath"] = typeof(string),
            ["FirstSequence"] = typeof(long),
            ["LastSequence"] = typeof(long),
            ["FirstMonotonicTimestamp"] = typeof(long),
            ["LastMonotonicTimestamp"] = typeof(long),
            ["EventCount"] = typeof(long),
            ["CanonicalLength"] = typeof(long),
            ["CompressedLength"] = typeof(long),
            ["FileLength"] = typeof(long),
            ["CompressedCrc32C"] = typeof(uint),
            ["CanonicalSha256"] = typeof(string),
            ["CreatedUtcMicroseconds"] = typeof(long),
            ["Recovered"] = typeof(bool)
        });
    }

    [Fact]
    public void ChunkPersistenceSourcesAndVersionTwoSqlExcludeForbiddenIdentifiers()
    {
        string root = FindRepositoryRoot();
        string[] productFiles =
        [
            Path.Combine(root, "src", "InputTracker.Storage", "MouseChunk.cs"),
            Path.Combine(root, "src", "InputTracker.Storage", "MouseChunkCatalog.cs"),
            Path.Combine(root, "src", "InputTracker.Storage", "MouseChunkBuilder.cs")
        ];
        string source = string.Join('\n', productFiles.Select(File.ReadAllText));
        string storageProject = File.ReadAllText(Path.Combine(root, "src", "InputTracker.Storage", "InputTracker.Storage.csproj"));
        string[] forbidden =
        [
            "InterfacePath", "HardwareSummary", "Alias", "Keyboard", "Window", "Process",
            "Focus", "Text", "RuntimeDeviceId"
        ];

        foreach (string identifier in forbidden)
        {
            Assert.DoesNotMatch(new Regex($@"\b{Regex.Escape(identifier)}\b", RegexOptions.CultureInvariant), source);
            Assert.DoesNotMatch(
                new Regex(
                    $@"\b{Regex.Escape(ToSnakeCase(identifier))}\s+(INTEGER|TEXT|BLOB|REAL)\b",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
                StorageSchema.Version2Sql);
        }

        Assert.DoesNotContain("InputTracker.Input", storageProject, StringComparison.Ordinal);
    }

    private static void AssertProperties<T>(IReadOnlyDictionary<string, Type> expected)
    {
        Dictionary<string, Type> actual = typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(property => property.Name, property => property.PropertyType, StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
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

    private static string ToSnakeCase(string value) =>
        Regex.Replace(value, "(?<!^)([A-Z])", "_$1", RegexOptions.CultureInvariant).ToLowerInvariant();
}
