using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace InputTracker.Storage.Tests;

public sealed class DurabilitySpikeTests
{
    private const int MixedEventCount = 100_000;
    private const int MouseEquivalentEventCount = 30_000;
    private const int BatchSize = 1_024;

    [Fact]
    [Trait("Category", "StorageSpike")]
    public void MeasuresRealDurabilityStrategiesAndWritesMachineReadableEvidence()
    {
        string repositoryRoot = FindRepositoryRoot();
        string evidenceDirectory = System.IO.Path.Combine(
            repositoryRoot,
            ".tools",
            "storage-spike",
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}");
        Directory.CreateDirectory(evidenceDirectory);

        var runs = new List<SpikeResult>
        {
            MeasureSqlite(
                System.IO.Path.Combine(evidenceDirectory, "sqlite-autocommit"),
                "A-sqlite-autocommit",
                MixedEventCount,
                batchSize: 1),
            MeasureSqlite(
                System.IO.Path.Combine(evidenceDirectory, "sqlite-batched"),
                "B-sqlite-bounded-batch",
                MixedEventCount,
                BatchSize),
            MeasureSpool(
                System.IO.Path.Combine(evidenceDirectory, "crc-spool-mixed"),
                "C-crc-spool-boundary-flush",
                "mixed-100000",
                MixedEventCount,
                allMouse: false),
            MeasureSpool(
                System.IO.Path.Combine(evidenceDirectory, "crc-spool-1000hz-30s"),
                "C-crc-spool-boundary-flush",
                "mouse-1000hz-30s-equivalent",
                MouseEquivalentEventCount,
                allMouse: true)
        };

        Assert.All(runs, run =>
        {
            Assert.Equal(run.Accepted, run.SinkAcknowledged);
            Assert.Equal(run.Accepted, run.SqliteCommitted);
            Assert.Equal(run.Accepted, run.PowerDurable);
            Assert.Equal(run.Accepted, run.Recovered);
            Assert.Equal(0, run.Lost);
            Assert.Equal(0, run.Corrupt);
            Assert.True(run.EventsPerSecond > 0);
            Assert.True(run.BytesPerEvent > 0);
        });

        Assert.Equal(MixedEventCount, runs[0].Accepted);
        Assert.Equal(MixedEventCount, runs[1].Accepted);
        Assert.Equal(MixedEventCount, runs[2].Accepted);
        Assert.Equal(MouseEquivalentEventCount, runs[3].Accepted);
        Assert.Equal(MouseEquivalentEventCount, runs[3].SpoolAppended);

        var evidence = new
        {
            schemaVersion = 1,
            createdUtc = DateTimeOffset.UtcNow,
            sdkRequested = "10.0.302",
            runtimeVersion = Environment.Version.ToString(),
            operatingSystem = Environment.OSVersion.ToString(),
            logicalProcessorCount = Environment.ProcessorCount,
            microsoftDataSqliteAssemblyVersion = typeof(SqliteConnection).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            dependencyGraph = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Microsoft.Data.Sqlite"] = "10.0.10",
                ["Microsoft.Data.Sqlite.Core"] = "10.0.10",
                ["SQLitePCLRaw.bundle_e_sqlite3"] = "2.1.11",
                ["SQLitePCLRaw.core"] = "2.1.11",
                ["SQLitePCLRaw.lib.e_sqlite3"] = "2.1.11",
                ["SQLitePCLRaw.provider.e_sqlite3"] = "2.1.11"
            },
            sqliteVersion = runs.Select(run => run.SqliteVersion).Distinct(StringComparer.Ordinal).Single(),
            hardwareTerminalStatus = "not-run",
            powerTestKind = "storage-layer-contract",
            releaseEligible = false,
            countSemantics = new
            {
                accepted = "Every generated event presented to the strategy; no sampling.",
                sinkAcknowledged = "The strategy returned from its per-event append or SQL operation; not fsync.",
                sqliteCommitted = "Events represented after a successful SQLite transaction; C includes the committed spool directory watermark.",
                spoolAppended = "Mouse records appended to the CRC spool; zero for SQLite-only strategies.",
                powerDurable = "Events at the completed strategy durability boundary; not evidence of a physical power-cut test.",
                recovered = "Events counted after closing and reopening the SQLite database or CRC spool.",
                lost = "Precisely detected incomplete-tail records; zero in clean benchmark runs.",
                corrupt = "CRC-invalid records; zero in clean benchmark runs and fail-closed in recovery tests."
            },
            runs
        };

        string jsonPath = System.IO.Path.Combine(evidenceDirectory, "storage-durability-spike.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));
        Console.WriteLine($"STORAGE_SPIKE_JSON={jsonPath}");
    }

    private static SpikeResult MeasureSqlite(string directory, string strategy, int eventCount, int batchSize)
    {
        Directory.CreateDirectory(directory);
        string databasePath = System.IO.Path.Combine(directory, "spike.sqlite3");
        using (var connection = OpenSpikeDatabase(databasePath))
        {
            CreateSpikeSchema(connection);
        }

        long accepted = 0;
        long acknowledged = 0;
        long committed = 0;
        var acknowledgementTicks = new long[eventCount];
        var elapsed = Stopwatch.StartNew();
        using (var connection = OpenSpikeDatabase(databasePath))
        {
            SqliteTransaction? transaction = batchSize == 1 ? null : connection.BeginTransaction();
            int pending = 0;
            try
            {
                for (int index = 0; index < eventCount; index++)
                {
                    accepted++;
                    long started = Stopwatch.GetTimestamp();
                    if ((index & 1) == 0)
                    {
                        UpsertKeyboard(connection, transaction, index);
                    }
                    else
                    {
                        InsertMouse(connection, transaction, index);
                    }

                    acknowledgementTicks[index] = Stopwatch.GetTimestamp() - started;
                    acknowledged++;
                    pending++;

                    if (batchSize == 1)
                    {
                        committed++;
                    }
                    else if (pending == batchSize)
                    {
                        transaction!.Commit();
                        committed += pending;
                        transaction.Dispose();
                        transaction = connection.BeginTransaction();
                        pending = 0;
                    }
                }

                if (batchSize > 1 && pending > 0)
                {
                    transaction!.Commit();
                    committed += pending;
                }
            }
            finally
            {
                transaction?.Dispose();
            }
        }

        elapsed.Stop();
        long recovered;
        string sqliteVersion;
        using (var connection = OpenSpikeDatabase(databasePath))
        {
            sqliteVersion = ScalarString(connection, "SELECT sqlite_version();");
            recovered = ScalarInt64(connection, "SELECT COALESCE(SUM(press_count), 0) FROM keyboard_daily;")
                + ScalarInt64(connection, "SELECT COUNT(*) FROM mouse_events;");
        }

        return Result(
            strategy,
            "mixed-100000",
            sqliteVersion,
            accepted,
            acknowledged,
            committed,
            spoolAppended: 0,
            powerDurable: committed,
            recovered,
            lost: 0,
            corrupt: 0,
            elapsed.Elapsed,
            acknowledgementTicks,
            DirectorySize(directory),
            batchSize == 1 ? "sqlite-full-autocommit" : $"sqlite-full-transaction-{batchSize}");
    }

    private static SpikeResult MeasureSpool(
        string programDirectory,
        string strategy,
        string workload,
        int eventCount,
        bool allMouse)
    {
        StorageLayout layout = StorageLayout.Create(programDirectory);
        Guid keyboardDevice = Guid.NewGuid();
        Guid mouseDevice = Guid.NewGuid();
        string spoolPath = System.IO.Path.Combine(layout.SpoolDirectory, "mouse.open");
        long accepted = 0;
        long acknowledged = 0;
        long keyboardCommitted = 0;
        long mouseCount = 0;
        long durable = 0;
        long flushMilliseconds;
        string sqliteVersion;
        var acknowledgementTicks = new long[eventCount];
        var elapsed = Stopwatch.StartNew();

        using (StorageDatabase database = StorageDatabase.Open(layout))
        using (var writer = new MouseSpoolWriter(spoolPath))
        {
            sqliteVersion = database.SqliteVersion;
            database.RegisterDevice(keyboardDevice, kind: 1, identityConfidence: 0, createdUtcMicroseconds: 1);
            database.RegisterDevice(mouseDevice, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
            SqliteTransaction transaction = database.Connection.BeginTransaction();
            int pendingKeyboard = 0;
            try
            {
                for (int index = 0; index < eventCount; index++)
                {
                    bool mouse = allMouse || (index & 1) != 0;
                    accepted++;
                    long started = Stopwatch.GetTimestamp();
                    if (mouse)
                    {
                        mouseCount++;
                        writer.Append(MouseRecord(mouseCount));
                    }
                    else
                    {
                        UpsertProductionKeyboard(database.Connection, transaction, keyboardDevice, index);
                        pendingKeyboard++;
                    }

                    acknowledgementTicks[index] = Stopwatch.GetTimestamp() - started;
                    acknowledged++;

                    if (pendingKeyboard == BatchSize)
                    {
                        transaction.Commit();
                        keyboardCommitted += pendingKeyboard;
                        transaction.Dispose();
                        transaction = database.Connection.BeginTransaction();
                        pendingKeyboard = 0;
                    }
                }

                transaction.Commit();
                keyboardCommitted += pendingKeyboard;
            }
            finally
            {
                transaction.Dispose();
            }

            var flush = Stopwatch.StartNew();
            SpoolDurableWatermark watermark = database.CommitSpoolBoundary(
                mouseDevice,
                writer,
                $"spool-{workload}",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000);
            flush.Stop();
            flushMilliseconds = flush.ElapsedMilliseconds;
            durable = keyboardCommitted + watermark.PowerDurable;
        }

        elapsed.Stop();
        SpoolRecoveryResult recovery = MouseSpoolRecovery.Recover(spoolPath, truncateIncompleteTail: false);
        long recoveredKeyboard;
        long sqliteMouseCommitted;
        using (StorageDatabase database = StorageDatabase.Open(layout))
        {
            recoveredKeyboard = ScalarInt64(database.Connection, "SELECT COALESCE(SUM(press_count), 0) FROM keyboard_daily;");
            sqliteMouseCommitted = ScalarInt64(
                database.Connection,
                $"SELECT record_count FROM mouse_spool_commits WHERE spool_id = 'spool-{workload}';");
        }

        return Result(
            strategy,
            workload,
            sqliteVersion,
            accepted,
            acknowledged,
            keyboardCommitted + sqliteMouseCommitted,
            mouseCount,
            durable,
            recoveredKeyboard + recovery.Recovered,
            recovery.Lost,
            recovery.Corrupt,
            elapsed.Elapsed,
            acknowledgementTicks,
            DirectorySize(programDirectory),
            $"mouse-flush-true-then-sqlite-full-watermark; boundaryFlushMs={flushMilliseconds}");
    }

    private static SpikeResult Result(
        string strategy,
        string workload,
        string sqliteVersion,
        long accepted,
        long sinkAcknowledged,
        long sqliteCommitted,
        long spoolAppended,
        long powerDurable,
        long recovered,
        long lost,
        long corrupt,
        TimeSpan elapsed,
        long[] acknowledgementTicks,
        long bytes,
        string durabilityBoundary)
    {
        Array.Sort(acknowledgementTicks);
        double tickToMicroseconds = 1_000_000d / Stopwatch.Frequency;
        return new SpikeResult(
            strategy,
            workload,
            durabilityBoundary,
            sqliteVersion,
            accepted,
            sinkAcknowledged,
            sqliteCommitted,
            spoolAppended,
            powerDurable,
            recovered,
            lost,
            corrupt,
            accepted / elapsed.TotalSeconds,
            acknowledgementTicks[PercentileIndex(acknowledgementTicks.Length, 0.50)] * tickToMicroseconds,
            acknowledgementTicks[PercentileIndex(acknowledgementTicks.Length, 0.95)] * tickToMicroseconds,
            bytes / (double)accepted,
            bytes,
            elapsed.TotalMilliseconds);
    }

    private static SqliteConnection OpenSpikeDatabase(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void CreateSpikeSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE keyboard_daily (
                device_id TEXT NOT NULL,
                physical_key INTEGER NOT NULL,
                capture_local_date TEXT NOT NULL,
                press_count INTEGER NOT NULL,
                hold_duration_us INTEGER NOT NULL,
                PRIMARY KEY(device_id, physical_key, capture_local_date)
            ) WITHOUT ROWID, STRICT;
            CREATE TABLE mouse_events (
                sequence INTEGER PRIMARY KEY,
                monotonic_timestamp INTEGER NOT NULL,
                delta_x INTEGER NOT NULL,
                delta_y INTEGER NOT NULL,
                motion_kind INTEGER NOT NULL,
                uses_virtual_desktop INTEGER NOT NULL,
                button_changes INTEGER NOT NULL,
                vertical_wheel_delta INTEGER NOT NULL,
                horizontal_wheel_delta INTEGER NOT NULL
            ) STRICT;
            """;
        command.ExecuteNonQuery();
    }

    private static void UpsertKeyboard(SqliteConnection connection, SqliteTransaction? transaction, int index)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO keyboard_daily(device_id, physical_key, capture_local_date, press_count, hold_duration_us)
            VALUES ('keyboard', $key, '2026-08-05', 1, 1000)
            ON CONFLICT(device_id, physical_key, capture_local_date)
            DO UPDATE SET press_count = press_count + 1, hold_duration_us = hold_duration_us + 1000;
            """;
        command.Parameters.AddWithValue("$key", index % 104);
        command.ExecuteNonQuery();
    }

    private static void InsertMouse(SqliteConnection connection, SqliteTransaction? transaction, int index)
    {
        MouseSpoolRecord record = MouseRecord((index / 2) + 1L);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mouse_events(
                sequence, monotonic_timestamp, delta_x, delta_y, motion_kind, uses_virtual_desktop,
                button_changes, vertical_wheel_delta, horizontal_wheel_delta)
            VALUES ($sequence, $timestamp, $x, $y, $motion, $desktop, $buttons, $vertical, $horizontal);
            """;
        command.Parameters.AddWithValue("$sequence", record.Sequence);
        command.Parameters.AddWithValue("$timestamp", record.MonotonicTimestamp);
        command.Parameters.AddWithValue("$x", record.DeltaX);
        command.Parameters.AddWithValue("$y", record.DeltaY);
        command.Parameters.AddWithValue("$motion", record.MotionKind);
        command.Parameters.AddWithValue("$desktop", record.UsesVirtualDesktop ? 1 : 0);
        command.Parameters.AddWithValue("$buttons", record.ButtonChanges);
        command.Parameters.AddWithValue("$vertical", record.VerticalWheelDelta);
        command.Parameters.AddWithValue("$horizontal", record.HorizontalWheelDelta);
        command.ExecuteNonQuery();
    }

    private static void UpsertProductionKeyboard(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid deviceId,
        int index)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO keyboard_daily(
                device_id, physical_key, capture_local_date, time_zone_id, press_count, hold_duration_us)
            VALUES ($device, $key, '2026-08-05', 'UTC', 1, 1000)
            ON CONFLICT(device_id, physical_key, capture_local_date, time_zone_id)
            DO UPDATE SET press_count = press_count + 1, hold_duration_us = hold_duration_us + 1000;
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$key", index % 104);
        command.ExecuteNonQuery();
    }

    private static MouseSpoolRecord MouseRecord(long sequence) => new(
        sequence,
        Stopwatch.GetTimestamp(),
        (int)(sequence % 11) - 5,
        (int)(sequence % 7) - 3,
        (byte)(sequence & 1),
        (sequence & 2) != 0,
        (ushort)(sequence % 1024),
        (short)(sequence % 3 == 0 ? 120 : 0),
        (short)(sequence % 5 == 0 ? -120 : 0));

    private static long ScalarInt64(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static int PercentileIndex(int count, double percentile) =>
        Math.Clamp((int)Math.Ceiling(count * percentile) - 1, 0, count - 1);

    private static long DirectorySize(string path) => Directory
        .EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Sum(file => new FileInfo(file).Length);

    private static string FindRepositoryRoot()
    {
        string? root = FindRepositoryRoot(Environment.CurrentDirectory)
            ?? FindRepositoryRoot(AppContext.BaseDirectory);
        return root ?? throw new DirectoryNotFoundException("Could not find InputTracker.sln above the test directory.");
    }

    private static string? FindRepositoryRoot(string start)
    {
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "InputTracker.sln")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private sealed record SpikeResult(
        string Strategy,
        string Workload,
        string DurabilityBoundary,
        string SqliteVersion,
        long Accepted,
        long SinkAcknowledged,
        long SqliteCommitted,
        long SpoolAppended,
        long PowerDurable,
        long Recovered,
        long Lost,
        long Corrupt,
        double EventsPerSecond,
        double P50AcknowledgementMicroseconds,
        double P95AcknowledgementMicroseconds,
        double BytesPerEvent,
        long TotalBytes,
        double ElapsedMilliseconds);
}
