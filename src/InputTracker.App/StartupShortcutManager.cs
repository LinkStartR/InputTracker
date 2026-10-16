using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace InputTracker;

internal sealed record StartupShortcutDescriptor(
    string TargetPath,
    string Arguments,
    string WorkingDirectory,
    string Description);

internal interface IStartupShortcutAccess
{
    void Create(string shortcutPath, StartupShortcutDescriptor descriptor);
    StartupShortcutDescriptor Read(string shortcutPath);
}

internal sealed class StartupShortcutManager
{
    internal const string OwnedDescription = "InputTracker owned startup shortcut v1";
    private readonly string _executablePath;
    private readonly string _startupDirectory;
    private readonly IStartupShortcutAccess _shortcutAccess;

    public StartupShortcutManager(
        string executablePath,
        string startupDirectory,
        IStartupShortcutAccess? shortcutAccess = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(startupDirectory);
        _executablePath = Path.GetFullPath(executablePath);
        _startupDirectory = Path.GetFullPath(startupDirectory);
        _shortcutAccess = shortcutAccess ?? new WindowsShellLinkAccess();
        ShortcutPath = Path.Combine(_startupDirectory, "InputTracker.lnk");
    }

    public string ShortcutPath { get; }

    public bool IsEnabled
    {
        get
        {
            EnsureSafeBoundaries();
            if (!File.Exists(ShortcutPath)) return false;
            if (!IsOwned(ReadExisting()))
            {
                throw new InvalidOperationException("The InputTracker startup shortcut belongs to another executable.");
            }

            return true;
        }
    }

    public void Enable()
    {
        EnsureSafeBoundaries();
        if (File.Exists(ShortcutPath))
        {
            if (IsOwned(ReadExisting())) return;
            throw new InvalidOperationException("The InputTracker startup shortcut is occupied and will not be overwritten.");
        }

        _shortcutAccess.Create(ShortcutPath, ExpectedDescriptor());
        if (!File.Exists(ShortcutPath) || !IsOwned(ReadExisting()))
        {
            throw new IOException("The startup shortcut could not be verified after creation.");
        }
    }

    public void Disable()
    {
        EnsureSafeBoundaries();
        if (!File.Exists(ShortcutPath)) return;
        if (!IsOwned(ReadExisting()))
        {
            throw new InvalidOperationException("The InputTracker startup shortcut belongs to another executable and will not be deleted.");
        }

        File.Delete(ShortcutPath);
    }

    private StartupShortcutDescriptor ReadExisting()
    {
        FileAttributes attributes = File.GetAttributes(ShortcutPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException("The startup shortcut must be a regular file.");
        }

        return _shortcutAccess.Read(ShortcutPath);
    }

    private void EnsureSafeBoundaries()
    {
        if (!File.Exists(_executablePath)) throw new FileNotFoundException("InputTracker executable is missing.", _executablePath);
        if (!Directory.Exists(_startupDirectory)) throw new DirectoryNotFoundException(_startupDirectory);
        if ((File.GetAttributes(_startupDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The Startup directory is a reparse point; no shortcut was changed.");
        }

        string relative = Path.GetRelativePath(_startupDirectory, ShortcutPath);
        if (!string.Equals(relative, "InputTracker.lnk", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The startup shortcut path escaped its exact boundary.");
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private StartupShortcutDescriptor ExpectedDescriptor() => new(
        _executablePath,
        string.Empty,
        Path.GetDirectoryName(_executablePath)!,
        OwnedDescription);

    private bool IsOwned(StartupShortcutDescriptor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StartupShortcutDescriptor expected = ExpectedDescriptor();
        return PathsEqual(value.TargetPath, expected.TargetPath)
            && string.Equals(value.Arguments, expected.Arguments, StringComparison.Ordinal)
            && PathsEqual(value.WorkingDirectory, expected.WorkingDirectory)
            && string.Equals(value.Description, expected.Description, StringComparison.Ordinal);
    }
}

internal sealed class WindowsShellLinkAccess : IStartupShortcutAccess
{
    public void Create(string shortcutPath, StartupShortcutDescriptor descriptor)
    {
        var link = (IShellLinkW)(object)new ShellLink();
        try
        {
            link.SetPath(descriptor.TargetPath);
            link.SetArguments(descriptor.Arguments);
            link.SetWorkingDirectory(descriptor.WorkingDirectory);
            link.SetDescription(descriptor.Description);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public StartupShortcutDescriptor Read(string shortcutPath)
    {
        var link = (IShellLinkW)(object)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0);
            var value = new StringBuilder(32_768);
            link.GetPath(value, value.Capacity, IntPtr.Zero, 0);
            if (value.Length == 0) throw new InvalidDataException("The startup shortcut has no executable target.");
            var arguments = new StringBuilder(32_768);
            link.GetArguments(arguments, arguments.Capacity);
            var workingDirectory = new StringBuilder(32_768);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            var description = new StringBuilder(1024);
            link.GetDescription(description, description.Capacity);
            return new StartupShortcutDescriptor(
                value.ToString(), arguments.ToString(), workingDirectory.ToString(), description.ToString());
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maximumPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maximumName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maximumPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximumPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr owner, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
