using System.IO;
using System.Text.Json;

namespace InputTracker;

internal sealed record PortableSettings(
    int SchemaVersion,
    bool CollectionConsentGranted,
    bool GamepadConsentGranted = false,
    bool GamepadCaptureEnabled = false,
    Guid? GamepadDeviceId = null);

internal sealed class PortableSettingsStore
{
    internal const string FileName = "InputTracker.settings.json";
    private const int CurrentSchemaVersion = 2;
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
            if (settings is null || settings.SchemaVersion is not 1 and not CurrentSchemaVersion)
            {
                throw new InvalidDataException("Portable settings schema is missing or unsupported.");
            }

            return settings with { SchemaVersion = CurrentSchemaVersion };
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

    public void GrantCollectionConsent() => Update(settings => settings with
    {
        CollectionConsentGranted = true
    });

    public void SetGamepadConsent(bool granted) => Update(settings => settings with
    {
        GamepadConsentGranted = granted,
        GamepadCaptureEnabled = granted && settings.GamepadCaptureEnabled,
        GamepadDeviceId = granted ? settings.GamepadDeviceId : null
    });

    public void SetGamepadConfiguration(bool enabled, Guid? deviceId = null) => Update(settings =>
    {
        if (enabled && (!settings.GamepadConsentGranted || deviceId is null || deviceId == Guid.Empty))
        {
            throw new InvalidOperationException("Gamepad consent and a selected device are required before enabling capture.");
        }

        return settings with
        {
            GamepadCaptureEnabled = enabled,
            GamepadDeviceId = enabled ? deviceId : settings.GamepadDeviceId
        };
    });

    private void Update(Func<PortableSettings, PortableSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        EnsureContainedRegularFileIfPresent();
        string temporaryPath = SettingsPath + ".new";
        EnsureContained(temporaryPath);
        if (File.Exists(temporaryPath) || Directory.Exists(temporaryPath))
        {
            throw new InvalidDataException("Portable settings staging path is already occupied.");
        }

        try
        {
            string json = JsonSerializer.Serialize(update(Load()), JsonOptions);
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
    public bool Confirm() => new ConsentWindow().ShowDialog() == true;
}
