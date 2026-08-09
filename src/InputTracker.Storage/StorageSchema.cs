using System.Security.Cryptography;
using System.Text;

namespace InputTracker.Storage;

internal static class StorageSchema
{
    public const int CurrentVersion = 3;

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

    public const string Version2Sql = """
        CREATE TABLE mouse_chunks (
            chunk_id TEXT PRIMARY KEY,
            device_id TEXT NOT NULL REFERENCES devices(device_id),
            calibration_segment_id INTEGER NOT NULL REFERENCES time_calibration_segments(segment_id),
            relative_path TEXT NOT NULL UNIQUE,
            first_sequence INTEGER NOT NULL CHECK (first_sequence > 0),
            last_sequence INTEGER NOT NULL CHECK (last_sequence >= first_sequence),
            first_monotonic_timestamp INTEGER NOT NULL CHECK (first_monotonic_timestamp >= 0),
            last_monotonic_timestamp INTEGER NOT NULL CHECK (last_monotonic_timestamp >= first_monotonic_timestamp),
            event_count INTEGER NOT NULL CHECK (
                event_count > 0 AND event_count = last_sequence - first_sequence + 1),
            canonical_length INTEGER NOT NULL CHECK (canonical_length > 0),
            compressed_length INTEGER NOT NULL CHECK (compressed_length > 0),
            file_length INTEGER NOT NULL CHECK (file_length > 0),
            compressed_crc32c INTEGER NOT NULL CHECK (compressed_crc32c >= 0),
            canonical_sha256 TEXT NOT NULL CHECK (length(canonical_sha256) = 64),
            created_utc_us INTEGER NOT NULL,
            recovered INTEGER NOT NULL CHECK (recovered IN (0, 1)),
            UNIQUE(device_id, calibration_segment_id, first_sequence, last_sequence)
        ) WITHOUT ROWID, STRICT;

        CREATE INDEX mouse_chunks_device_sequence
            ON mouse_chunks(device_id, calibration_segment_id, first_sequence, last_sequence);

        PRAGMA user_version = 2;
        """;

    public static string Version2Checksum { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Version2Sql)));

    public const string Version3Sql = """
        ALTER TABLE devices ADD COLUMN identity_ambiguity INTEGER NOT NULL DEFAULT 1
            CHECK (identity_ambiguity >= 0);
        ALTER TABLE devices ADD COLUMN last_seen_utc_us INTEGER;

        ALTER TABLE keyboard_daily ADD COLUMN make_code INTEGER
            GENERATED ALWAYS AS (physical_key & 65535) VIRTUAL;
        ALTER TABLE keyboard_daily ADD COLUMN is_e0 INTEGER
            GENERATED ALWAYS AS ((physical_key >> 16) & 1) VIRTUAL;
        ALTER TABLE keyboard_daily ADD COLUMN is_e1 INTEGER
            GENERATED ALWAYS AS ((physical_key >> 17) & 1) VIRTUAL;

        CREATE TABLE device_identity_mappings (
            interface_fingerprint BLOB PRIMARY KEY CHECK (length(interface_fingerprint) = 32),
            device_id TEXT NOT NULL REFERENCES devices(device_id),
            kind INTEGER NOT NULL,
            hardware_fingerprint BLOB NOT NULL CHECK (length(hardware_fingerprint) = 32),
            stable_hardware_fingerprint BLOB CHECK (
                stable_hardware_fingerprint IS NULL OR length(stable_hardware_fingerprint) = 32),
            first_seen_utc_us INTEGER NOT NULL,
            last_seen_utc_us INTEGER NOT NULL CHECK (last_seen_utc_us >= first_seen_utc_us)
        ) WITHOUT ROWID, STRICT;

        CREATE INDEX device_identity_stable_hardware
            ON device_identity_mappings(stable_hardware_fingerprint)
            WHERE stable_hardware_fingerprint IS NOT NULL;
        CREATE INDEX device_identity_device
            ON device_identity_mappings(device_id);

        PRAGMA user_version = 3;
        """;

    public static string Version3Checksum { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Version3Sql)));
}
