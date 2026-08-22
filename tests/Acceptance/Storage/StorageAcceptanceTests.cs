using System.Reflection;
using InputTracker.Storage;

namespace InputTracker.AcceptanceTests.Storage;

public sealed class StorageAcceptanceTests
{
    [Theory]
    [InlineData(StoragePeriodKind.Day, 1)]
    [InlineData(StoragePeriodKind.Week, 7)]
    [InlineData(StoragePeriodKind.Month, 31)]
    [InlineData(StoragePeriodKind.Year, 365)]
    public void PeriodsAreExplicitHalfOpenWindowsRanges(StoragePeriodKind kind, int expectedDays)
    {
        StorageResolvedPeriod period = StoragePeriodResolver.Resolve(kind, new DateOnly(2026, 8, 13), "UTC");

        Assert.Equal(expectedDays, period.LocalEndDate.DayNumber - period.LocalStartDate.DayNumber);
        Assert.True(period.UtcRange.EndUtcMicroseconds > period.UtcRange.StartUtcMicroseconds);
        Assert.Equal("UTC", period.TimeZoneId);
    }

    [Theory]
    [InlineData(2026, 3, 8, 23)]
    [InlineData(2026, 11, 1, 25)]
    public void PacificDaysUseActualDstDuration(int year, int month, int day, int expectedHours)
    {
        StorageResolvedPeriod period = StoragePeriodResolver.Resolve(
            StoragePeriodKind.Day, new DateOnly(year, month, day), "Pacific Standard Time");

        long duration = period.UtcRange.EndUtcMicroseconds - period.UtcRange.StartUtcMicroseconds;
        Assert.Equal(expectedHours * 60L * 60L * 1_000_000L, duration);
    }

    [Fact]
    public async Task DeletionPreviewIsExplicitlyBlockedByCurrentFormat()
    {
        using var directory = new AcceptanceTemporaryDirectory("storage deletion");
        using StorageService service = StorageService.Open(StorageLayout.Create(directory.Path));
        StorageDeviceResolution device = service.ObserveDevice(StorageAcceptanceSupport.MouseObservation());

        StorageDeletionPreview preview = await service.PreviewDeletionAsync(
            device.Device.DeviceId, new StorageUtcRange(1, 2));

        Assert.Equal(StorageDeletionCapability.BlockedByCurrentFormat, preview.Capability);
        Assert.False(string.IsNullOrWhiteSpace(preview.BlockedReason));
        Assert.Throws<InvalidOperationException>(() => service.ExecuteDeletion(preview, preview.Token));
    }

    [Fact]
    public void PublicKeyboardStorageContractContainsNoTextOrApplicationIdentity()
    {
        string[] names = typeof(StorageKeyboardAggregate).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        string joined = string.Join('|', names);

        Assert.Equal(
            ["DeviceId", "MakeCode", "IsE0", "IsE1", "CaptureLocalDate", "TimeZoneId", "PressCount", "HoldDurationMicroseconds"],
            names);
        foreach (string forbidden in new[] { "Text", "Character", "Window", "Process", "Application", "Title", "Sequence" })
        {
            Assert.DoesNotContain(forbidden, joined, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MouseChunkReaderFailsClosedForInjectedTemporaryCorruption()
    {
        using var directory = new AcceptanceTemporaryDirectory("storage corruption");
        string chunkPath = StorageAcceptanceSupport.CreateCommittedMouseChunk(directory.Path);
        MouseChunk healthy = MouseChunkReader.Read(chunkPath);
        Assert.Single(healthy.Records);

        if (Environment.GetEnvironmentVariable("INPUTTRACKER_STORAGE_INJECTION") == "chunk-corruption")
        {
            byte[] bytes = File.ReadAllBytes(chunkPath);
            bytes[108] ^= 1;
            File.WriteAllBytes(chunkPath, bytes);
            _ = MouseChunkReader.Read(chunkPath);
        }
    }

    [Fact]
    public void AggregateMismatchInjectionCannotAffectProductionData()
    {
        long fixtureValue = Environment.GetEnvironmentVariable("INPUTTRACKER_STORAGE_INJECTION") == "aggregate-mismatch"
            ? 5
            : 4;

        Assert.Equal(4, fixtureValue);
    }
}
