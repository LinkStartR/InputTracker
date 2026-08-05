using System.Security.Cryptography;
using System.Text;

namespace InputTracker.Storage;

internal static class StorageSchema
{
    public const int CurrentVersion = 1;

    public const string Version1Sql = """
        CREATE TABLE schema_migrations (
            version INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE,
            checksum TEXT NOT NULL,
            applied_utc_us INTEGER NOT NULL
        ) STRICT;

        CREATE TABLE store_metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) WITHOUT ROWID, STRICT;

        CREATE TABLE clock_sessions (
            session_id TEXT PRIMARY KEY,
            frequency INTEGER NOT NULL CHECK (frequency > 0),
            created_utc_us INTEGER NOT NULL
        ) WITHOUT ROWID, STRICT;

        CREATE TABLE time_calibration_segments (
            segment_id INTEGER PRIMARY KEY,
            session_id TEXT NOT NULL REFERENCES clock_sessions(session_id),
            monotonic_anchor INTEGER NOT NULL,
            utc_anchor_us INTEGER NOT NULL,
            uncertainty_ticks INTEGER NOT NULL CHECK (uncertainty_ticks >= 0),
            time_zone_id TEXT NOT NULL
        ) STRICT;

        CREATE TABLE devices (
            device_id TEXT PRIMARY KEY,
            kind INTEGER NOT NULL,
            identity_confidence INTEGER NOT NULL,
            identity_fingerprint BLOB,
            alias TEXT,
            created_utc_us INTEGER NOT NULL
        ) WITHOUT ROWID, STRICT;

        CREATE TABLE keyboard_daily (
            device_id TEXT NOT NULL REFERENCES devices(device_id),
            physical_key INTEGER NOT NULL,
            capture_local_date TEXT NOT NULL,
            time_zone_id TEXT NOT NULL,
            press_count INTEGER NOT NULL CHECK (press_count >= 0),
            hold_duration_us INTEGER NOT NULL CHECK (hold_duration_us >= 0),
            PRIMARY KEY (device_id, physical_key, capture_local_date, time_zone_id)
        ) WITHOUT ROWID, STRICT;

        CREATE TABLE mouse_spool_commits (
            spool_id TEXT PRIMARY KEY,
            device_id TEXT NOT NULL REFERENCES devices(device_id),
            committed_length INTEGER NOT NULL CHECK (committed_length >= 0),
            record_count INTEGER NOT NULL CHECK (record_count >= 0),
            power_durable_count INTEGER NOT NULL CHECK (power_durable_count >= 0),
            updated_utc_us INTEGER NOT NULL
        ) WITHOUT ROWID, STRICT;

        PRAGMA user_version = 1;
        """;

    public static string Version1Checksum { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Version1Sql)));
}
