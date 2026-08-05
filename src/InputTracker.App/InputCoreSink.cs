using InputTracker.Input;

namespace InputTracker;

internal sealed class InputCoreSink : IInputEventSink
{
    private long _keyboardTransitions;
    private long _mousePackets;
    private long _deviceChanges;
    private long _boundaries;

    public ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (input.Kind)
        {
            case CapturedInputKind.Keyboard:
                Interlocked.Increment(ref _keyboardTransitions);
                break;
            case CapturedInputKind.Mouse:
                Interlocked.Increment(ref _mousePackets);
                break;
            case CapturedInputKind.DeviceChange:
                Interlocked.Increment(ref _deviceChanges);
                break;
            case CapturedInputKind.AggregationBoundary:
                Interlocked.Increment(ref _boundaries);
                break;
        }

        return ValueTask.CompletedTask;
    }
}
