namespace InputTracker.Storage;

public static class StorageOfflineRestore
{
    public static StorageRestoreResult RestoreOffline(
        StorageLayout layout,
        string relativeBackupPath)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ValidateCanonicalLayout(layout);
        return StorageBackup.Restore(layout, relativeBackupPath);
    }

    private static void ValidateCanonicalLayout(StorageLayout layout)
    {
        string programDirectory = Path.GetFullPath(layout.ProgramDirectory);
        string storageDirectory = Path.Combine(programDirectory, "data", "storage");
        if (!PathEquals(layout.StorageDirectory, storageDirectory)
            || !PathEquals(layout.DatabasePath, Path.Combine(storageDirectory, "store.sqlite3"))
            || !PathEquals(layout.SpoolDirectory, Path.Combine(storageDirectory, "spool"))
            || !PathEquals(layout.ChunkDirectory, Path.Combine(storageDirectory, "chunks")))
        {
            throw new InvalidDataException("Offline restore requires the canonical Storage layout.");
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
