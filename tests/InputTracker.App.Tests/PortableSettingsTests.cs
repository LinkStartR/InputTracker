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

        File.WriteAllText(path, "{\"schemaVersion\":2,\"collectionConsentGranted\":true}");
        Assert.Throws<InvalidDataException>(() => new PortableSettingsStore(temporary.Path).Load());
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
