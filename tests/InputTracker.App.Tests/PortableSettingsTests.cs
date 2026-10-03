namespace InputTracker.App.Tests;

public sealed class PortableSettingsTests
{
    [Fact]
    public void DeclinedConsentDoesNotWriteSettings()
    {
        using var temporary = new TemporaryDirectory();
        var store = new PortableSettingsStore(temporary.Path);

        bool granted = FirstRunConsent.EnsureGranted(store, new FixedPrompt(false));

        Assert.False(granted);
        Assert.Empty(Directory.GetFiles(temporary.Path));
    }

    [Fact]
    public void GrantedConsentIsPromptedOnceAndPersisted()
    {
        using var temporary = new TemporaryDirectory();
        var store = new PortableSettingsStore(temporary.Path);
        var prompt = new FixedPrompt(true);

        Assert.True(FirstRunConsent.EnsureGranted(store, prompt));
        Assert.True(FirstRunConsent.EnsureGranted(store, prompt));
        Assert.Equal(1, prompt.Count);
    }

    [Fact]
    public void MissingSettingsRequireConsentAndSaveIsProgramLocal()
    {
        using var temporary = new TemporaryDirectory();
        var store = new PortableSettingsStore(temporary.Path);

        Assert.False(store.Load().CollectionConsentGranted);

        store.GrantCollectionConsent();

        Assert.True(store.Load().CollectionConsentGranted);
        Assert.Equal(
            Path.Combine(temporary.Path, PortableSettingsStore.FileName),
            store.SettingsPath);
        Assert.Single(Directory.GetFiles(temporary.Path));
    }

    [Fact]
    public void MalformedOrFutureSettingsFailClosed()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, PortableSettingsStore.FileName);
        File.WriteAllText(path, "not-json");
        Assert.Throws<InvalidDataException>(() => new PortableSettingsStore(temporary.Path).Load());

        File.WriteAllText(path, "{\"schemaVersion\":4,\"collectionConsentGranted\":true}");
        Assert.Throws<InvalidDataException>(() => new PortableSettingsStore(temporary.Path).Load());
    }

    [Fact]
    public void VersionOneSettingsUpgradeInMemoryWithoutGrantingGamepadConsent()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, PortableSettingsStore.FileName);
        const string original = "{\"schemaVersion\":1,\"collectionConsentGranted\":true}";
        File.WriteAllText(path, original);
        var store = new PortableSettingsStore(temporary.Path);

        PortableSettings loaded = store.Load();

        Assert.Equal(3, loaded.SchemaVersion);
        Assert.True(loaded.CollectionConsentGranted);
        Assert.False(loaded.GamepadConsentGranted);
        Assert.False(loaded.GamepadCaptureEnabled);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void GamepadSettingsRequireSeparateConsentAndPreserveKeyboardConsent()
    {
        using var temporary = new TemporaryDirectory();
        var store = new PortableSettingsStore(temporary.Path);
        store.GrantCollectionConsent();
        Guid deviceId = Guid.NewGuid();

        Assert.False(store.Load().GamepadConsentGranted);
        Assert.Throws<InvalidOperationException>(() => store.SetGamepadConfiguration(true, deviceId));

        store.SetGamepadConsent(true);
        store.SetGamepadConfiguration(true, deviceId);
        store.SetGamepadConsent(false);

        PortableSettings loaded = store.Load();
        Assert.True(loaded.CollectionConsentGranted);
        Assert.False(loaded.GamepadConsentGranted);
        Assert.False(loaded.GamepadCaptureEnabled);
        Assert.Null(loaded.GamepadDeviceId);
        Assert.DoesNotContain("interfacePath", File.ReadAllText(store.SettingsPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionTwoSettingsUpgradeAndRememberBrandsPerStorageDeviceId()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, PortableSettingsStore.FileName);
        Guid firstDevice = Guid.NewGuid();
        Guid secondDevice = Guid.NewGuid();
        File.WriteAllText(path,
            $"{{\"schemaVersion\":2,\"collectionConsentGranted\":true,\"gamepadConsentGranted\":true,\"gamepadCaptureEnabled\":false,\"gamepadDeviceId\":\"{firstDevice:D}\"}}");
        var store = new PortableSettingsStore(temporary.Path);

        Assert.Equal(3, store.Load().SchemaVersion);
        store.SetGamepadBrand(firstDevice, GamepadBrand.Xbox);
        store.SetGamepadBrand(secondDevice, GamepadBrand.ThirdParty);
        store.SetGamepadConfiguration(false);

        PortableSettings loaded = store.Load();
        Assert.Equal(firstDevice, loaded.GamepadDeviceId);
        Assert.False(loaded.GamepadCaptureEnabled);
        Assert.Equal(GamepadBrand.Xbox, store.GetGamepadBrand(firstDevice));
        Assert.Equal(GamepadBrand.ThirdParty, store.GetGamepadBrand(secondDevice));
        string json = File.ReadAllText(store.SettingsPath);
        Assert.Contains(firstDevice.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("interfacePath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hardwareId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serial", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RevokingGamepadConsentClearsRememberedBrands()
    {
        using var temporary = new TemporaryDirectory();
        var store = new PortableSettingsStore(temporary.Path);
        store.SetGamepadConsent(true);
        Guid deviceId = Guid.NewGuid();
        store.SetGamepadBrand(deviceId, GamepadBrand.Xbox);

        store.SetGamepadConsent(false);

        Assert.Null(store.GetGamepadBrand(deviceId));
        Assert.Empty(store.Load().GamepadBrands!);
    }

    [Fact]
    public void ReparseSettingsFileIsRejected()
    {
        using var temporary = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        string link = Path.Combine(temporary.Path, "LinkedProgram");
        TemporaryDirectory.CreateJunction(link, outside.Path);
        File.WriteAllText(
            Path.Combine(outside.Path, PortableSettingsStore.FileName),
            "{\"schemaVersion\":1,\"collectionConsentGranted\":true}");

        Assert.Throws<InvalidDataException>(() => new PortableSettingsStore(link).Load());
        Directory.Delete(link);
    }

    private sealed class FixedPrompt(bool answer) : IPortableConsentPrompt
    {
        public int Count { get; private set; }
        public bool Confirm()
        {
            Count++;
            return answer;
        }
    }
}
