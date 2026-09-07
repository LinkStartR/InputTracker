using System.IO;
using System.Text.Json;
using System.Windows;

namespace InputTracker;

internal sealed record PortableSettings(int SchemaVersion, bool CollectionConsentGranted);

internal sealed class PortableSettingsStore
{
    internal const string FileName = "InputTracker.settings.json";
    private const int CurrentSchemaVersion = 1;
    private readonly string _programDirectory;

    public PortableSettingsStore(string programDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDirectory);
        _programDirectory = Path.GetFullPath(programDirectory);
        SettingsPath = Path.Combine(_programDirectory, FileName);
    }

    public string SettingsPath { get; }

    public PortableSettings Load()
    {
        EnsureContainedRegularFileIfPresent();
        if (!File.Exists(SettingsPath)) return new PortableSettings(CurrentSchemaVersion, false);

        try
        {
            PortableSettings? settings = JsonSerializer.Deserialize<PortableSettings>(
                File.ReadAllText(SettingsPath), JsonOptions);
            if (settings is null || settings.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException("Portable settings schema is missing or unsupported.");
            }

            return settings;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Portable settings could not be read safely.", error);
        }
    }

    public void GrantCollectionConsent()
    {
        EnsureContainedRegularFileIfPresent();
        string temporaryPath = SettingsPath + ".new";
        EnsureContained(temporaryPath);
        if (File.Exists(temporaryPath) || Directory.Exists(temporaryPath))
        {
            throw new InvalidDataException("Portable settings staging path is already occupied.");
        }

        try
        {
            string json = JsonSerializer.Serialize(
                new PortableSettings(CurrentSchemaVersion, true), JsonOptions);
            using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private void EnsureContainedRegularFileIfPresent()
    {
        if (!Directory.Exists(_programDirectory)
            || (File.GetAttributes(_programDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Portable settings program directory must not be a reparse point.");
        }

        EnsureContained(SettingsPath);
        if (!File.Exists(SettingsPath) && !Directory.Exists(SettingsPath)) return;
        FileAttributes attributes = File.GetAttributes(SettingsPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException("Portable settings path must be a regular file in the program directory.");
        }
    }

    private void EnsureContained(string path)
    {
        string relative = Path.GetRelativePath(_programDirectory, Path.GetFullPath(path));
        if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative == ".."
            || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("Portable settings path escaped the program directory.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false
    };
}

internal interface IPortableConsentPrompt
{
    bool Confirm();
}

internal static class FirstRunConsent
{
    public static bool EnsureGranted(PortableSettingsStore settings, IPortableConsentPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(prompt);
        if (settings.Load().CollectionConsentGranted) return true;
        if (!prompt.Confirm()) return false;
        settings.GrantCollectionConsent();
        return settings.Load().CollectionConsentGranted;
    }
}

internal sealed class WpfPortableConsentPrompt : IPortableConsentPrompt
{
    public bool Confirm() => System.Windows.MessageBox.Show(
        "InputTracker 会在后台记录键盘和鼠标活动。\n\n"
        + "键盘只统计每个设备上实体按键的次数和按住时长，不保存输入内容、先后顺序、窗口或程序名称。\n"
        + "鼠标只统计各按键的次数，不保存移动位置或路线。旧版本已经保存的轨迹文件不会自动删除。\n"
        + "你可以随时暂停或退出，也能在界面中查看数据位置。确认前不会开始记录。\n\n"
        + "现在开始记录吗？",
        "InputTracker · 开始记录前确认",
        System.Windows.MessageBoxButton.YesNo,
        System.Windows.MessageBoxImage.Information,
        System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;
}
