using System.Text.Json;

namespace InputTracker.AcceptanceTests.InputCore;

internal static class InputCoreAcceptanceEvidence
{
    private const string EvidenceDirectoryVariable = "INPUTTRACKER_INPUTCORE_EVIDENCE_DIRECTORY";

    public static bool IsOutOfOrderInjectionEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("INPUTTRACKER_INPUTCORE_INJECTION"),
            "out-of-order",
            StringComparison.Ordinal);

    public static void Write(string name, object report)
    {
        string? directory = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, $"{name}.json");
        string temporary = Path.Combine(directory, $".{name}-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(report));
        File.Move(temporary, destination, overwrite: true);
    }

    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InputTracker.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

internal sealed class AcceptanceDeviceInfoProvider : IRawDeviceInfoProvider
{
    public RawDeviceMetadata Query(nint handle, RawDeviceKind hintedKind) => new(
        hintedKind,
        $"acceptance://{handle}",
        $"acceptance-{hintedKind}",
        DeviceIdentityConfidence.SessionOnly);
}

internal sealed class CountingHashSink : IInputEventSink
{
    private ulong _hash = 1469598103934665603UL;

    public long Count { get; private set; }

    public ulong Hash => _hash;

    public ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken)
    {
        Count++;
        _hash ^= (ulong)input.Kind + 0x9E3779B97F4A7C15UL;
        _hash *= 1099511628211UL;
        return ValueTask.CompletedTask;
    }
}

internal sealed class AcceptanceFakeHostFactory : IRawInputHostFactory
{
    public AcceptanceFakeHost Host { get; private set; } = null!;

    public IRawInputHost Create(
        BoundedInputQueue queue,
        InputPipelineCounters counters,
        Action<string> onIntegrityFault,
        Action<bool> onRegistrationChanged)
    {
        Host = new AcceptanceFakeHost(queue, onIntegrityFault, onRegistrationChanged);
        return Host;
    }
}

internal sealed class AcceptanceFakeHost(
    BoundedInputQueue queue,
    Action<string> onIntegrityFault,
    Action<bool> onRegistrationChanged) : IRawInputHost
{
    private bool _registered;

    public bool IngressOpen { get; private set; }
    public bool StopAfterFaultCalled { get; private set; }
    public bool Destroyed { get; private set; }
    public bool BoundaryAcknowledged { get; private set; }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        Register();
        IngressOpen = true;
        return ValueTask.CompletedTask;
    }

    public void CloseIngress() => IngressOpen = false;

    public async ValueTask PauseAsync(PauseReason reasons, CancellationToken cancellationToken)
    {
        Unregister();
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(queue.TryWrite(new InputIngressEvent(
            IngressEventKind.Boundary,
            0,
            1,
            BoundaryKind: AggregationBoundaryKind.Paused,
            PauseReasons: reasons,
            Barrier: barrier)));
        await barrier.Task.WaitAsync(cancellationToken);
        BoundaryAcknowledged = true;
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        Register();
        IngressOpen = true;
        return ValueTask.CompletedTask;
    }

    public async ValueTask PrepareStopAsync(bool enqueueBarrier, CancellationToken cancellationToken)
    {
        Unregister();
        if (enqueueBarrier)
        {
            var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(queue.TryWrite(new InputIngressEvent(
                IngressEventKind.Boundary,
                0,
                1,
                BoundaryKind: AggregationBoundaryKind.Stopping,
                Barrier: barrier)));
            await barrier.Task.WaitAsync(cancellationToken);
            BoundaryAcknowledged = true;
        }
    }

    public ValueTask DestroyAsync(CancellationToken cancellationToken)
    {
        Destroyed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAfterFaultAsync(CancellationToken cancellationToken)
    {
        StopAfterFaultCalled = true;
        CloseIngress();
        Unregister();
        return ValueTask.CompletedTask;
    }

    public bool Inject(InputIngressEvent input)
    {
        bool accepted = queue.TryWrite(input);
        if (!accepted)
        {
            CloseIngress();
            Unregister();
            onIntegrityFault("acceptance queue overflow");
        }

        return accepted;
    }

    private void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        onRegistrationChanged(true);
    }

    private void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        onRegistrationChanged(false);
    }
}
