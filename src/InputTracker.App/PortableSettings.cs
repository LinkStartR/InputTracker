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
        "InputTracker 将在后台接收物理键盘和鼠标输入。\n\n"
        + "键盘仅累计设备、物理键和日期的次数/时长，不保存文字、事件顺序、窗口或进程。\n"
        + "鼠标原始轨迹按设备保存在程序目录；当前测试版尚未提供范围删除，请只产生可接受保留的数据。\n"
        + "你可随时暂停、退出，并在界面查看数据位置。未确认前不会启动采集。\n\n"
        + "是否确认并启动采集？",
        "InputTracker · 首次采集确认",
        System.Windows.MessageBoxButton.YesNo,
        System.Windows.MessageBoxImage.Information,
        System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;
}
