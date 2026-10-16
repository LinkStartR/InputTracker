using System.Reflection;
using InputTracker.Input.Win32;

namespace InputTracker.Input.Tests;

public sealed class DeviceIdentityTests
{
    [Theory]
    [InlineData("BTHENUM", "BTHENUM\\DEV_001", (int)DeviceTransportAssessment.Bluetooth)]
    [InlineData("ROOT", "ROOT\\RDP_MOU", (int)DeviceTransportAssessment.RemoteDesktop)]
    [InlineData("VMBUS", "VMBUS\\{DEVICE}", (int)DeviceTransportAssessment.KnownVirtual)]
    [InlineData("USB", "HID\\VID_1234&PID_5678", (int)DeviceTransportAssessment.Physical)]
    public void TransportClassificationUsesExplicitDeviceEvidence(
        string enumerator,
        string hardwareId,
        int expectedValue)
    {
        DeviceTransportAssessment actual = DeviceIdentityClassifier.Classify(
            interfacePath: "\\\\?\\HID#DEVICE",
            hardwareIds: [hardwareId],
            enumeratorName: enumerator,
            busTypeGuid: Guid.NewGuid(),
            classificationComplete: true);

        Assert.Equal((DeviceTransportAssessment)expectedValue, actual);
    }

    [Fact]
    public void MissingClassificationPropertiesRemainIndeterminate()
    {
        DeviceTransportAssessment result = DeviceIdentityClassifier.Classify(
            "\\\\?\\HID#VID_1234&PID_5678",
            ["HID\\VID_1234&PID_5678"],
            "USB",
            Guid.NewGuid(),
            classificationComplete: false);

        Assert.Equal(DeviceTransportAssessment.Indeterminate, result);
    }

    [Fact]
    public void PropertyDecoderReadsMultiStringStringAndGuidWithoutGuessingInstanceSerial()
    {
        byte[] multi = System.Text.Encoding.Unicode.GetBytes("HID\\B\0HID\\A\0\0");
        byte[] text = System.Text.Encoding.Unicode.GetBytes("SERIAL-42\0");
        Guid container = Guid.NewGuid();

        Assert.Equal(["HID\\B", "HID\\A"], DeviceIdentityPropertyDecoder.MultiString(multi));
        Assert.Equal("SERIAL-42", DeviceIdentityPropertyDecoder.String(text));
        Assert.Equal(container, DeviceIdentityPropertyDecoder.Guid(container.ToByteArray()));
        Assert.Null(DeviceIdentityPropertyDecoder.String([]));
    }

    [Fact]
    public async Task WorkerPublishesPrivateIdentityOncePerArrivalGeneration()
    {
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(16, counters);
        var sink = new RecordingSink();
        var observer = new RecordingIdentityObserver();
        var provider = new IdentityProvider();
        var worker = new InputWorker(
            queue,
            counters,
            sink,
            provider,
            error => ValueTask.FromException(error),
            identityObserver: observer);
        Task run = worker.Start();
        nint handle = new(51);

        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Mouse, handle, 1, DeltaX: 1)));
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Mouse, handle, 2, DeltaX: 2)));
        Assert.True(queue.TryWrite(new InputIngressEvent(
            IngressEventKind.DeviceRemoved,
            handle,
            3,
            DeviceKind: RawDeviceKind.Mouse)));
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Mouse, handle, 4, DeltaX: 3)));
        queue.Complete();
        await run;

        Assert.Equal(2, provider.QueryCount);
        Assert.Equal(2, observer.Items.Count);
        Assert.NotEqual(observer.Items[0].RuntimeId, observer.Items[1].RuntimeId);
        Assert.All(observer.Items, item => Assert.Equal("PRIVATE-SERIAL", item.Identity.SerialNumber));
        Assert.All(
            sink.Items.Where(item => item.Kind == CapturedInputKind.DeviceChange),
            item =>
            {
                Assert.Null(item.DeviceChange.Device.InterfacePath);
                Assert.DoesNotContain(
                    item.DeviceChange.Device.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance),
                    property => property.Name is "HardwareIds" or "SerialNumber" or "ContainerId");
            });
    }

    [Fact]
    public async Task DuplicateExplicitArrivalDoesNotRepeatIdentityQueryForActiveGeneration()
    {
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(8, counters);
        var sink = new RecordingSink();
        var observer = new RecordingIdentityObserver();
        var provider = new IdentityProvider();
        var worker = new InputWorker(
            queue,
            counters,
            sink,
            provider,
            error => ValueTask.FromException(error),
            identityObserver: observer);
        Task run = worker.Start();
        nint handle = new(52);

        Assert.True(queue.TryWrite(new InputIngressEvent(
            IngressEventKind.DeviceArrived,
            handle,
            1,
            DeviceKind: RawDeviceKind.Keyboard)));
        Assert.True(queue.TryWrite(new InputIngressEvent(
            IngressEventKind.DeviceArrived,
            handle,
            2,
            DeviceKind: RawDeviceKind.Keyboard)));
        queue.Complete();
        await run;

        Assert.Equal(1, provider.QueryCount);
        Assert.Single(observer.Items);
        Assert.Single(sink.Items, item =>
            item.Kind == CapturedInputKind.DeviceChange &&
            item.DeviceChange.Change == DeviceChangeKind.Arrived);
    }

    private sealed class RecordingSink : IInputEventSink
    {
        public List<CapturedInput> Items { get; } = [];

        public ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken)
        {
            Items.Add(input);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingIdentityObserver : IRawDeviceIdentityObserver
    {
        public List<(RuntimeDeviceId RuntimeId, RawDeviceIdentityObservation Identity)> Items { get; } = [];

        public ValueTask ObserveAsync(
            RuntimeDeviceId runtimeId,
            RawDeviceIdentityObservation identity,
            CancellationToken cancellationToken)
        {
            Items.Add((runtimeId, identity));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IdentityProvider : IRawDeviceInfoProvider
    {
        public int QueryCount { get; private set; }

        public RawDeviceMetadata Query(nint deviceHandle, RawDeviceKind hintedKind)
        {
            QueryCount++;
            return new RawDeviceMetadata(
                hintedKind,
                InterfacePath: null,
                HardwareSummary: "mouse",
                DeviceIdentityConfidence.RequiresConfirmation,
                new RawDeviceIdentityObservation(
                    hintedKind,
                    "PRIVATE-INTERFACE",
                    ["HID\\PRIVATE-HARDWARE-ID"],
                    "PRIVATE-SERIAL",
                    Guid.NewGuid(),
                    DeviceTransportAssessment.Physical,
                    DeviceIdentityEvidence.RawInputInterfacePath |
                    DeviceIdentityEvidence.DevNodeHardwareIds |
                    DeviceIdentityEvidence.ContainerId |
                    DeviceIdentityEvidence.EnumeratorName));
        }
    }
}
