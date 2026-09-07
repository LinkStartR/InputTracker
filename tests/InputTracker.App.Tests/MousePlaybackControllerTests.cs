namespace InputTracker.App.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using InputTracker.Storage;

public sealed class MousePlaybackControllerTests
{
    [Fact]
    public async Task MillionRecordsAreProcessedInOrderWithBoundedQueueAndTail()
    {
        Guid device = Guid.NewGuid();
        var snapshot = new FakePlaybackSnapshot(device, CreateRecords(device, 1_000_000));
        await using var controller = new MousePlaybackController(new FakePlaybackDataSource(snapshot));

        await controller.LoadAsync(device, Range);
        await controller.PlayAsync();
        await controller.DrainAsync();

        MousePlaybackDiagnostics result = controller.Diagnostics;
        Assert.Equal(1_000_000, result.ProducedCount);
        Assert.Equal(1_000_000, result.ProcessedCount);
        Assert.Equal(0, result.OrderFailures);
        Assert.InRange(result.QueueDepth, 0, MousePlaybackController.MaxPrefetchRecords);
        Assert.InRange(result.ActiveTailCount, 0, MousePlaybackController.MaxTailPoints);
        Assert.Equal(snapshot.RollingHash, result.RollingHash);
    }

    [Fact]
    public async Task SeekAtSameUtcResumesAtExactCursorWithoutDuplicateOrOmission()
    {
        Guid device = Guid.NewGuid();
        var records = CreateRecords(device, 8, sameUtc: true);
        var snapshot = new FakePlaybackSnapshot(device, records);
        await using var controller = new MousePlaybackController(new FakePlaybackDataSource(snapshot));

        await controller.LoadAsync(device, Range);
        await controller.SeekAsync(records[3].Cursor);
        await controller.PlayAsync();
        await controller.DrainAsync();

        Assert.Equal(records.Skip(3).Select(value => value.Cursor.Sequence), controller.ProcessedSequences);
        Assert.Equal(5, controller.Diagnostics.ProcessedCount);
        Assert.Equal(8, controller.Diagnostics.PositionCount);
        Assert.Equal(0, controller.Diagnostics.OrderFailures);
    }

    [Fact]
    public async Task SpeedChangesKeepTheNextCursorContinuous()
    {
        Guid device = Guid.NewGuid();
        var records = CreateRecords(device, 8, stepUtc: 1_000_000);
        await using var controller = new MousePlaybackController(
            new FakePlaybackDataSource(new FakePlaybackSnapshot(device, records)));

        await controller.LoadAsync(device, Range);
        await controller.PlayAsync();
        await controller.AdvanceByAsync(TimeSpan.FromSeconds(1));
        await ProcessUntilAsync(controller, 2);
        controller.SetSpeed(MousePlaybackSpeed.Quarter);
        await controller.AdvanceByAsync(TimeSpan.FromSeconds(4));
        await ProcessUntilAsync(controller, 3);
        controller.SetSpeed(MousePlaybackSpeed.Four);
        await controller.AdvanceByAsync(TimeSpan.FromSeconds(1));
        await ProcessUntilAsync(controller, 7);

        Assert.Equal(Enumerable.Range(1, 7).Select(value => (long)value), controller.ProcessedSequences);
        Assert.Equal(0, controller.Diagnostics.OrderFailures);
        Assert.Equal(MousePlaybackSpeed.Four, controller.Diagnostics.Speed);
    }

    [Fact]
    public async Task PauseReasonsComposeAndWindowHiddenDoesNotTouchCapture()
    {
        Guid device = Guid.NewGuid();
        await using var controller = new MousePlaybackController(
            new FakePlaybackDataSource(new FakePlaybackSnapshot(device, CreateRecords(device, 8))));

        await controller.LoadAsync(device, Range);
        await controller.PlayAsync();
        controller.Pause(MousePlaybackPauseReason.User);
        controller.Pause(MousePlaybackPauseReason.WindowHidden);
        controller.Resume(MousePlaybackPauseReason.User);

        Assert.True(controller.Diagnostics.PauseReasons.HasFlag(MousePlaybackPauseReason.WindowHidden));
        Assert.Equal(MousePlaybackState.Paused, controller.Diagnostics.State);
        Assert.Equal(0, controller.CapturePauseRequests);

        controller.Resume(MousePlaybackPauseReason.WindowHidden);
        Assert.Equal(MousePlaybackState.Playing, controller.Diagnostics.State);
        Assert.Equal(0, controller.CapturePauseRequests);
    }

    [Fact]
    public async Task OneConsumeBatchRaisesAtMostOneControllerChangedNotification()
    {
        Guid device = Guid.NewGuid();
        await using var controller = new MousePlaybackController(
            new FakePlaybackDataSource(new FakePlaybackSnapshot(device, CreateRecords(device, 1_024))));
        await controller.LoadAsync(device, Range);
        await controller.PlayAsync();
        await WaitUntilAsync(() => controller.Diagnostics.ProducedCount == 1_024);
        int changed = 0;
        controller.Changed += (_, _) => Interlocked.Increment(ref changed);

        await controller.AdvanceByAsync(TimeSpan.FromSeconds(1), maxRecords: 512);

        Assert.Equal(512, controller.Diagnostics.ProcessedCount);
        Assert.InRange(Volatile.Read(ref changed), 0, 1);
    }

    [Fact]
    public async Task SeekWaitsForOldProducerAndStartsANewGenerationWithoutStaleCounters()
    {
        Guid device = Guid.NewGuid();
        StorageMousePlaybackRecord[] records = CreateRecords(device, 5);
        var snapshot = new BlockingPlaybackSnapshot(device, records);
        await using var controller = new MousePlaybackController(new FakePlaybackDataSource(snapshot));
        await controller.LoadAsync(device, Range);
        await controller.PlayAsync();
        await snapshot.FirstRecordWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task seek = controller.SeekAsync(records[2].Cursor);
        await Task.Delay(20);
        Assert.False(seek.IsCompleted);
        snapshot.AllowContinuation.SetResult();
        await seek;
        await controller.PlayAsync();
        await controller.DrainAsync();

        Assert.Equal([3L, 4L, 5L], controller.ProcessedSequences);
        Assert.Equal(3, controller.Diagnostics.ProducedCount);
        Assert.Equal(3, controller.Diagnostics.ProcessedCount);
        Assert.True(controller.Diagnostics.Generation >= 2);
    }

    [Fact]
    public async Task SequenceGapAfterTheFirstSeekRecordFaultsAndDrainTerminates()
    {
        Guid device = Guid.NewGuid();
        Guid snapshot = Guid.NewGuid();
        StorageMousePlaybackRecord[] records =
        [
            Playback(device, snapshot, 1, 10, 1, 1, 0, false, true, StorageMousePlaybackBoundaryReason.RangeStart),
            Playback(device, snapshot, 3, 11, 1, 1, 0, false, false, StorageMousePlaybackBoundaryReason.RangeStart)
        ];
        await using var controller = new MousePlaybackController(
            new FakePlaybackDataSource(new FakePlaybackSnapshot(device, records)));

        await controller.LoadAsync(device, Range);
        await controller.PlayAsync();
        await controller.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MousePlaybackState.Faulted, controller.Diagnostics.State);
        Assert.Equal(1, controller.Diagnostics.OrderFailures);
        Assert.Equal(1, controller.Diagnostics.ProcessedCount);
    }

    [Fact]
    public async Task CatchingUpRequiresAnEvidenceBacklogRatherThanAnOpenProducer()
    {
        Guid device = Guid.NewGuid();
        await using var controller = new MousePlaybackController(
            new FakePlaybackDataSource(new FakePlaybackSnapshot(device, CreateRecords(device, 600))));
        await controller.LoadAsync(device, Range);
        await controller.PlayAsync();
        await WaitUntilAsync(() => controller.Diagnostics.ProducedCount == 600);

        await controller.AdvanceByAsync(TimeSpan.FromSeconds(1), maxRecords: 512);
        Assert.True(controller.Diagnostics.IsCatchingUp);
        await controller.DrainAsync();
        Assert.False(controller.Diagnostics.IsCatchingUp);
    }

    [Fact]
    public async Task UncalibratedAbsoluteDisclosureSurvivesAfterThePointLeavesTheBoundedTailAndResetsOnSeek()
    {
        Guid device = Guid.NewGuid();
        Guid snapshot = Guid.NewGuid();
        StorageMousePlaybackRecord[] records = Enumerable.Range(1, 2_050)
            .Select(index => Playback(
                device, snapshot, index, index, 1, 1,
                motionKind: index == 1 ? (byte)1 : (byte)0,
                virtualDesktop: index == 1,
                startsNew: index == 1,
                reason: StorageMousePlaybackBoundaryReason.RangeStart))
            .ToArray();
        await using var controller = new MousePlaybackController(
            new FakePlaybackDataSource(new FakePlaybackSnapshot(device, records)));

        await controller.LoadAsync(device, Range);
        await controller.PlayAsync();
        await controller.DrainAsync();

        Assert.DoesNotContain(controller.ActiveTail, point => point.MotionKind != 0);
        Assert.True(controller.Diagnostics.HasUncalibratedAbsolute);

        await controller.SeekAsync(records[2].Cursor);
        await controller.PlayAsync();
        await controller.DrainAsync();

        Assert.False(controller.Diagnostics.HasUncalibratedAbsolute);
    }

    [Fact]
    public async Task NewLoadCancelsOldGenerationAndRejectsStaleRecords()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        var firstSnapshot = new FakePlaybackSnapshot(first, CreateRecords(first, 100), yieldDelay: true);
        var secondSnapshot = new FakePlaybackSnapshot(second, CreateRecords(second, 3));
        await using var controller = new MousePlaybackController(
            new FakePlaybackDataSource(firstSnapshot, secondSnapshot));

        await controller.LoadAsync(first, Range);
        await controller.PlayAsync();
        await controller.LoadAsync(second, Range);
        await controller.PlayAsync();
        await controller.DrainAsync();

        Assert.Equal(second, controller.Diagnostics.DeviceId);
        Assert.All(controller.ProcessedSequences, sequence => Assert.InRange(sequence, 1, 3));
        Assert.Equal(3, controller.Diagnostics.ProcessedCount);
        Assert.True(controller.Diagnostics.Generation >= 2);
    }

    [Fact]
    public async Task ForeignSnapshotCursorIsRejectedBeforeItCanRestartPlayback()
    {
        Guid device = Guid.NewGuid();
        StorageMousePlaybackRecord[] records = CreateRecords(device, 3);
        await using var controller = new MousePlaybackController(
            new FakePlaybackDataSource(new FakePlaybackSnapshot(device, records)));

        await controller.LoadAsync(device, Range);
        var foreign = records[1].Cursor with { SnapshotId = Guid.NewGuid() };

        await Assert.ThrowsAsync<InvalidDataException>(() => controller.SeekAsync(foreign));
        Assert.Equal(MousePlaybackState.Ready, controller.Diagnostics.State);
        Assert.Equal(0, controller.Diagnostics.ProcessedCount);
    }

    [Fact]
    public void RollingHashCoversButtonWheelMotionAndVirtualDesktopFields()
    {
        Guid device = Guid.NewGuid();
        Guid snapshot = Guid.NewGuid();
        StorageMousePlaybackRecord baseRecord = Playback(
            device, snapshot, 1, 10, 1, 2, motionKind: 0, virtualDesktop: false, startsNew: true,
            StorageMousePlaybackBoundaryReason.RangeStart);
        StorageMousePlaybackRecord changed = baseRecord with
        {
            Record = baseRecord.Record with
            {
                MotionKind = 1,
                UsesVirtualDesktop = true,
                ButtonChanges = 0x0003,
                VerticalWheelDelta = -120,
                HorizontalWheelDelta = 120
            }
        };

        Assert.NotEqual(
            MousePlaybackController.AddToRollingHash(0, baseRecord),
            MousePlaybackController.AddToRollingHash(0, changed));
    }

    [Fact]
    public void PresentationStringsDoNotCallBoundedOverviewFullPlayback()
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "InputTracker.App", "MainWindow.xaml"));
        Assert.Contains("画面一次最多显示 2048 个点", source, StringComparison.Ordinal);
        Assert.Contains("回放仍会按完整记录进行", source, StringComparison.Ordinal);
        Assert.DoesNotContain("时间轴播放仍在开发中", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackButtonsResumeOnlyTheUserReasonAndPauseTheFrameTimer()
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "InputTracker.App", "MainWindow.xaml.cs"));
        Assert.Contains("_mousePlayback.Resume(MousePlaybackPauseReason.User)", source, StringComparison.Ordinal);
        Assert.Contains("_mousePlaybackFrameTimer.Stop();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackUiUsesActualMonotonicElapsedTimeAndDoesNotOverwriteAnActiveTailWithOverview()
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "InputTracker.App", "MainWindow.xaml.cs"));
        Assert.Contains("Stopwatch.GetElapsedTime(previousTimestamp, currentTimestamp)", source, StringComparison.Ordinal);
        Assert.Contains("_mousePlayback.AdvanceByAsync(elapsed)", source, StringComparison.Ordinal);
        Assert.Contains("ResetMousePlaybackClock();", source, StringComparison.Ordinal);
        Assert.Contains("if (_mousePlayback is { ActiveTail.Count: > 0 } playback)", source, StringComparison.Ordinal);
        Assert.Contains("RenderMousePlayback(playback.ActiveTail)", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SegmentBoundariesAndUncalibratedAbsoluteMotionStayExplicit()
    {
        Guid device = Guid.NewGuid();
        Guid snapshot = Guid.NewGuid();
        StorageMousePlaybackRecord[] records =
        [
            Playback(device, snapshot, 1, 10, 3, 4, motionKind: 0, virtualDesktop: false, startsNew: true,
                StorageMousePlaybackBoundaryReason.RangeStart),
            Playback(device, snapshot, 2, 11, 65_535, 0, motionKind: 1, virtualDesktop: true, startsNew: true,
                StorageMousePlaybackBoundaryReason.CalibrationBoundary),
            Playback(device, snapshot, 3, 12, 5, 6, motionKind: 0, virtualDesktop: false, startsNew: true,
                StorageMousePlaybackBoundaryReason.PauseBoundary)
        ];
        await using var controller = new MousePlaybackController(
            new FakePlaybackDataSource(new FakePlaybackSnapshot(device, records)));

        await controller.LoadAsync(device, Range);
        await controller.PlayAsync();
        await controller.DrainAsync();

        IReadOnlyList<MousePlaybackRenderSegment> segments = MousePlaybackRenderer.BuildSegments(controller.ActiveTail);
        Assert.Equal(3, segments.Count);
        Assert.True(segments[1].IsUncalibratedAbsolute);
        Assert.Empty(segments[1].Points);
        Assert.Equal("绝对坐标未校准，保留原始记录但不伪造屏幕位置。", segments[1].Disclosure);
        Assert.All(controller.ActiveTail, point => Assert.True(point.StartsNewSegment));
    }

    private static readonly StorageUtcRange Range = new(0, 10_000_000_000);

    private static StorageMousePlaybackRecord[] CreateRecords(
        Guid device, int count, bool sameUtc = false, long stepUtc = 10)
    {
        Guid snapshot = Guid.NewGuid();
        return Enumerable.Range(1, count).Select(index =>
        {
            long utc = sameUtc ? 10 : checked(index * stepUtc);
            var cursor = new StorageMousePlaybackCursor(snapshot, index, utc);
            return new StorageMousePlaybackRecord(
                new StorageMouseRecord(device, index, utc, 1, 1, 0, false, 0, 0, 0),
                index == 1,
                index == 1 ? StorageMousePlaybackBoundaryReason.RangeStart : StorageMousePlaybackBoundaryReason.RangeStart,
                cursor);
        }).ToArray();
    }

    private static StorageMousePlaybackRecord Playback(
        Guid device, Guid snapshot, long sequence, long utc, int dx, int dy, byte motionKind,
        bool virtualDesktop, bool startsNew, StorageMousePlaybackBoundaryReason reason) =>
        new(new StorageMouseRecord(device, sequence, utc, dx, dy, motionKind, virtualDesktop, 0, 0, 0),
            startsNew, reason, new StorageMousePlaybackCursor(snapshot, sequence, utc));

    private static async Task ProcessUntilAsync(MousePlaybackController controller, int expected)
    {
        for (int attempt = 0; attempt < 100 && controller.Diagnostics.ProcessedCount < expected; attempt++)
        {
            await controller.AdvanceByAsync(TimeSpan.Zero);
            await Task.Delay(1);
        }
        Assert.Equal(expected, controller.Diagnostics.ProcessedCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (condition()) return;
            await Task.Delay(1);
        }
        Assert.True(condition(), "Timed out waiting for the expected playback state.");
    }

    private sealed class FakePlaybackDataSource(params IMousePlaybackSnapshot[] snapshots) : IMousePlaybackDataSource
    {
        public Task<IMousePlaybackSnapshot> OpenAsync(Guid deviceId, StorageUtcRange range, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMousePlaybackSnapshot snapshot = Assert.Single(snapshots, value => value.Info.DeviceId == deviceId);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakePlaybackSnapshot : IMousePlaybackSnapshot
    {
        private readonly StorageMousePlaybackRecord[] _records;
        private readonly bool _yieldDelay;
        public FakePlaybackSnapshot(Guid device, StorageMousePlaybackRecord[] records, bool yieldDelay = false)
        {
            _records = records;
            _yieldDelay = yieldDelay;
            Guid snapshotId = records.Length == 0 ? Guid.NewGuid() : records[0].Cursor.SnapshotId;
            Info = new StorageMousePlaybackSnapshotInfo(
                snapshotId, device, Range, records.Length, records.Length,
                records.FirstOrDefault().Cursor, records.LastOrDefault().Cursor);
            RollingHash = records.Aggregate(0UL, static (hash, value) => MousePlaybackController.AddToRollingHash(hash, value));
        }

        public ulong RollingHash { get; }
        public StorageMousePlaybackSnapshotInfo Info { get; }
        public async IAsyncEnumerable<StorageMousePlaybackRecord> StreamFromAsync(
            StorageMousePlaybackCursor? inclusiveCursor,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (StorageMousePlaybackRecord record in _records.Where(value => inclusiveCursor is null || value.Cursor.Sequence >= inclusiveCursor.Value.Sequence))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record;
                if (_yieldDelay) await Task.Yield();
            }
        }

        public ValueTask<StorageMousePlaybackCursor?> SeekAsync(long targetUtcMicroseconds, CancellationToken cancellationToken) =>
            ValueTask.FromResult<StorageMousePlaybackCursor?>(_records.FirstOrDefault(value => value.Cursor.UtcMicroseconds >= targetUtcMicroseconds).Cursor);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingPlaybackSnapshot(Guid device, StorageMousePlaybackRecord[] records) : IMousePlaybackSnapshot
    {
        public TaskCompletionSource FirstRecordWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowContinuation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StorageMousePlaybackSnapshotInfo Info { get; } = new(
            records[0].Cursor.SnapshotId, device, Range, records.Length, records.Length,
            records[0].Cursor, records[^1].Cursor);

        public async IAsyncEnumerable<StorageMousePlaybackRecord> StreamFromAsync(
            StorageMousePlaybackCursor? inclusiveCursor,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (StorageMousePlaybackRecord record in records.Where(value => inclusiveCursor is null || value.Cursor.Sequence >= inclusiveCursor.Value.Sequence))
            {
                yield return record;
                FirstRecordWritten.TrySetResult();
                await AllowContinuation.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        public ValueTask<StorageMousePlaybackCursor?> SeekAsync(long targetUtcMicroseconds, CancellationToken cancellationToken) =>
            ValueTask.FromResult<StorageMousePlaybackCursor?>(records.FirstOrDefault(value => value.Cursor.UtcMicroseconds >= targetUtcMicroseconds).Cursor);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
