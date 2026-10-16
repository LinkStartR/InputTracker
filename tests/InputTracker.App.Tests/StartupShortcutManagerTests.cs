namespace InputTracker.App.Tests;

public sealed class StartupShortcutManagerTests
{
    [Fact]
    public void EnableAndDisableAreExactAndIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        string executable = Path.Combine(temporary.Path, "InputTracker.exe");
        File.WriteAllText(executable, string.Empty);
        string startup = Path.Combine(temporary.Path, "Startup");
        Directory.CreateDirectory(startup);
        var shortcuts = new TextShortcutAccess();
        var manager = new StartupShortcutManager(executable, startup, shortcuts);

        manager.Enable();
        manager.Enable();
        Assert.True(manager.IsEnabled);
        Assert.Equal(
            new StartupShortcutDescriptor(
                Path.GetFullPath(executable),
                string.Empty,
                Path.GetDirectoryName(Path.GetFullPath(executable))!,
                StartupShortcutManager.OwnedDescription),
            shortcuts.Read(manager.ShortcutPath));

        manager.Disable();
        manager.Disable();
        Assert.False(File.Exists(manager.ShortcutPath));
    }

    [Fact]
    public void ForeignShortcutIsNeverOverwrittenOrDeleted()
    {
        using var temporary = new TemporaryDirectory();
        string executable = Path.Combine(temporary.Path, "InputTracker.exe");
        string foreign = Path.Combine(temporary.Path, "Other.exe");
        File.WriteAllText(executable, string.Empty);
        File.WriteAllText(foreign, string.Empty);
        string startup = Path.Combine(temporary.Path, "Startup");
        Directory.CreateDirectory(startup);
        var shortcuts = new TextShortcutAccess();
        var manager = new StartupShortcutManager(executable, startup, shortcuts);
        shortcuts.Create(
            manager.ShortcutPath,
            new StartupShortcutDescriptor(
                foreign, string.Empty, temporary.Path, StartupShortcutManager.OwnedDescription));

        Assert.Throws<InvalidOperationException>(manager.Enable);
        Assert.Throws<InvalidOperationException>(manager.Disable);
        Assert.True(File.Exists(manager.ShortcutPath));
        Assert.Equal(Path.GetFullPath(foreign), shortcuts.Read(manager.ShortcutPath).TargetPath);
    }

    [Theory]
    [InlineData("--foreign", null, null)]
    [InlineData(null, "foreign-working-directory", null)]
    [InlineData(null, null, "foreign-description")]
    public void SameTargetWithAnyForeignPropertyIsNeverOwned(
        string? arguments,
        string? alternateWorkingDirectory,
        string? description)
    {
        using var temporary = new TemporaryDirectory();
        string executable = Path.Combine(temporary.Path, "InputTracker.exe");
        File.WriteAllText(executable, string.Empty);
        string startup = Path.Combine(temporary.Path, "Startup");
        Directory.CreateDirectory(startup);
        var shortcuts = new TextShortcutAccess();
        var manager = new StartupShortcutManager(executable, startup, shortcuts);
        string workingDirectory = alternateWorkingDirectory is null
            ? temporary.Path
            : Path.Combine(temporary.Path, alternateWorkingDirectory);
        shortcuts.Create(
            manager.ShortcutPath,
            new StartupShortcutDescriptor(
                executable,
                arguments ?? string.Empty,
                workingDirectory,
                description ?? StartupShortcutManager.OwnedDescription));

        Assert.Throws<InvalidOperationException>(() => _ = manager.IsEnabled);
        Assert.Throws<InvalidOperationException>(manager.Enable);
        Assert.Throws<InvalidOperationException>(manager.Disable);
        Assert.True(File.Exists(manager.ShortcutPath));
    }

    [Fact]
    public void ReparseShortcutFailsClosed()
    {
        using var temporary = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        string executable = Path.Combine(temporary.Path, "InputTracker.exe");
        File.WriteAllText(executable, string.Empty);
        string startup = Path.Combine(temporary.Path, "Startup");
        TemporaryDirectory.CreateJunction(startup, outside.Path);
        var manager = new StartupShortcutManager(executable, startup, new TextShortcutAccess());

        Assert.Throws<InvalidDataException>(() => _ = manager.IsEnabled);
        Assert.Throws<InvalidDataException>(manager.Disable);
        Assert.Empty(Directory.GetFiles(outside.Path));
        Directory.Delete(startup);
    }

    private sealed class TextShortcutAccess : IStartupShortcutAccess
    {
        public void Create(string shortcutPath, StartupShortcutDescriptor descriptor) =>
            File.WriteAllLines(shortcutPath,
            [
                Path.GetFullPath(descriptor.TargetPath),
                descriptor.Arguments,
                Path.GetFullPath(descriptor.WorkingDirectory),
                descriptor.Description
            ]);

        public StartupShortcutDescriptor Read(string shortcutPath)
        {
            string[] values = File.ReadAllLines(shortcutPath);
            return new StartupShortcutDescriptor(values[0], values[1], values[2], values[3]);
        }
    }
}
