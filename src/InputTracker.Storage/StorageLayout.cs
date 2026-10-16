namespace InputTracker.Storage;

public sealed record StorageLayout(
    string ProgramDirectory,
    string StorageDirectory,
    string DatabasePath,
    string SpoolDirectory,
    string ChunkDirectory)
{
    public static StorageLayout Create(string programDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDirectory);
        string root = Path.GetFullPath(programDirectory);
        string storage = Path.Combine(root, "data", "storage");
        var layout = new StorageLayout(
            root,
            storage,
            Path.Combine(storage, "store.sqlite3"),
            Path.Combine(storage, "spool"),
            Path.Combine(storage, "chunks"));

        Directory.CreateDirectory(layout.StorageDirectory);
        Directory.CreateDirectory(layout.SpoolDirectory);
        Directory.CreateDirectory(layout.ChunkDirectory);
        return layout;
    }
}
