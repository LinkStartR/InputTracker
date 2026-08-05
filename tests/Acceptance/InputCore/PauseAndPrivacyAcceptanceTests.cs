namespace InputTracker.AcceptanceTests.InputCore;

public sealed class PauseAndPrivacyAcceptanceTests
{
    [Fact]
    public async Task PauseReasonsStackAndBarrierDrainsBeforeResume()
    {
        var factory = new AcceptanceFakeHostFactory();
        var sink = new CountingHashSink();
        await using var service = new InputCaptureService(sink, null, factory);
        await service.StartAsync();

        Assert.True(factory.Host.Inject(new InputIngressEvent(
            IngressEventKind.Mouse,
            0,
            1,
            DeltaX: 5,
            DeltaY: -4)));
        await service.SetPauseReasonAsync(PauseReason.User, true);
        DiagnosticsSnapshot paused = service.Diagnostics;

        Assert.Equal(InputCaptureState.Paused, service.CurrentStatus.State);
        Assert.Equal(PauseReason.User, service.CurrentStatus.PauseReasons);
        Assert.True(factory.Host.BoundaryAcknowledged);
        Assert.Equal(paused.Accepted, paused.Dequeued);
        Assert.Equal(paused.Accepted, paused.SinkAcknowledged);
        Assert.False(paused.IngressOpen);
        Assert.False(paused.RawInputRegistered);

        await service.SetPauseReasonAsync(PauseReason.SessionLocked, true);
        await service.SetPauseReasonAsync(PauseReason.SystemSuspend, true);
        await service.SetPauseReasonAsync(PauseReason.SessionLocked, false);
        await service.SetPauseReasonAsync(PauseReason.SystemSuspend, false);
        Assert.Equal(InputCaptureState.Paused, service.CurrentStatus.State);
        Assert.Equal(PauseReason.User, service.CurrentStatus.PauseReasons);

        await service.SetPauseReasonAsync(PauseReason.User, false);
        Assert.Equal(InputCaptureState.Running, service.CurrentStatus.State);
        Assert.True(service.Diagnostics.IngressOpen);
        Assert.True(service.Diagnostics.RawInputRegistered);
        await service.StopAsync();

        InputCoreAcceptanceEvidence.Write("pause-barrier", new
        {
            completedPauseBarriers = paused.CompletedPauseBarriers,
            boundaryAccepted = paused.Accepted,
            boundaryDequeued = paused.Dequeued,
            boundaryAcknowledged = paused.SinkAcknowledged,
            reasonsAfterUnlock = PauseReason.User.ToString(),
            resumedOnlyAfterLastReason = true
        });
    }

    [Fact]
    public void KeyboardPublicSchemaAndSourcePolicyPreservePrivacyBoundary()
    {
        string root = InputCoreAcceptanceEvidence.FindRepositoryRoot();
        string contracts = Path.Combine(root, "src", "InputTracker.Input", "InputContracts.cs");
        string parser = Path.Combine(root, "src", "InputTracker.Input", "Win32", "RawInputParser.cs");
        string interop = Path.Combine(root, "src", "InputTracker.Input", "Win32", "RawInputInterop.cs");
        string[] expectedFields = ["DeviceId", "MakeCode", "Prefix", "Transition", "Timestamp"];
        string[] actualFields = typeof(KeyboardPhysicalEvent).GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();
        Assert.Equal(expectedFields.OrderBy(name => name), actualFields);

        string publicContracts = File.ReadAllText(contracts);
        string parserSource = File.ReadAllText(parser);
        string interopSource = File.ReadAllText(interop);
        string[] forbiddenPublicTokens = [
            "Character", "Text", "VKey", "VirtualKey", "Message", "KeyName", "Window", "Process", "Foreground", "Focus"
        ];
        foreach (string token in forbiddenPublicTokens)
        {
            Assert.DoesNotContain(token, publicContracts, StringComparison.Ordinal);
            Assert.DoesNotContain(token, parserSource, StringComparison.Ordinal);
        }

        string[] forbiddenCalls = ["ToUnicode(", "GetKeyNameText(", "WM_CHAR", "GetForegroundWindow("];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            foreach (string forbiddenCall in forbiddenCalls)
            {
                Assert.DoesNotContain(forbiddenCall, source, StringComparison.Ordinal);
            }

            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(interop), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.DoesNotMatch(@"\bVKey\b", source);
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(
                source,
                @"\b(?:raw|keyboard|nativeKeyboard|rawKeyboard)\s*(?:\.Data)?(?:\.Keyboard)?\s*\.\s*(?:VKey|Message)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }

        Assert.Contains("IgnoredVirtualKey", interopSource, StringComparison.Ordinal);
        Assert.Contains("IgnoredLegacyMessage", interopSource, StringComparison.Ordinal);
        InputCoreAcceptanceEvidence.Write("privacy", new
        {
            publicKeyboardFields = actualFields,
            interopAllowlist = "src/InputTracker.Input/Win32/RawInputInterop.cs",
            forbiddenCalls,
            forbiddenCallsFound = 0
        });
    }
}
