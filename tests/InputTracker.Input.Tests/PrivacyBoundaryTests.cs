using System.Reflection;
using InputTracker.Input.Win32;

namespace InputTracker.Input.Tests;

public sealed class PrivacyBoundaryTests
{
    [Fact]
    public void PublicInputContractsExposeNoTextWindowProcessOrSequenceFields()
    {
        string[] forbidden =
        [
            "Text", "Character", "VirtualKey", "LegacyMessage", "Window", "Process", "Foreground", "Sequence"
        ];
        Type[] contractTypes =
        [
            typeof(KeyboardPhysicalEvent),
            typeof(MouseRawEvent),
            typeof(CapturedInput),
            typeof(AggregationBoundaryEvent)
        ];

        foreach (Type type in contractTypes)
        {
            string[] names = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .ToArray();
            foreach (string token in forbidden)
            {
                Assert.DoesNotContain(names, name => name.Contains(token, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void ProductSourceContainsNoTextOrForegroundCaptureApis()
    {
        string root = FindRepositoryRoot();
        string[] forbidden = ["ToUnicode(", "GetKeyNameText(", "WM_CHAR", "GetForegroundWindow("];
        string[] productRoots =
        [
            Path.Combine(root, "src", "InputTracker.Input"),
            Path.Combine(root, "src", "InputTracker.App")
        ];

        foreach (string file in productRoots.SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)))
        {
            string source = File.ReadAllText(file);
            foreach (string token in forbidden)
            {
                Assert.DoesNotContain(token, source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void PublicGamepadHardwareSummaryOmitsHidIdentity()
    {
        var information = new RawInputInterop.NativeRawDeviceInfo
        {
            Type = RawInputInterop.RimTypeHid,
            Data = new RawInputInterop.NativeRawDeviceInfoData
            {
                Hid = new RawInputInterop.NativeHidDeviceInfo
                {
                    UsagePage = RawInputInterop.UsagePageGenericDesktop,
                    Usage = RawInputInterop.UsageGamepad,
                    VendorId = 0x1234,
                    ProductId = 0x5678
                }
            }
        };

        string summary = RawDeviceInfoProvider.CreateHardwareSummary(RawDeviceKind.Gamepad, information);

        Assert.Equal("gamepad", summary);
        Assert.DoesNotContain("1234", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("5678", summary, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InputTracker.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
