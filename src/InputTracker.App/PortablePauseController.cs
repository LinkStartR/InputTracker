using InputTracker.Input;

namespace InputTracker;

internal enum PortableSystemEvent
{
    SessionLocked,
    SessionUnlocked,
    SystemSuspending,
    SystemResumed
}

internal sealed class PortablePauseController(IInputCaptureControl capture)
{
    public ValueTask HandleAsync(PortableSystemEvent value, CancellationToken cancellationToken = default) => value switch
    {
        PortableSystemEvent.SessionLocked => capture.SetPauseReasonAsync(PauseReason.SessionLocked, true, cancellationToken),
        PortableSystemEvent.SessionUnlocked => capture.SetPauseReasonAsync(PauseReason.SessionLocked, false, cancellationToken),
        PortableSystemEvent.SystemSuspending => capture.SetPauseReasonAsync(PauseReason.SystemSuspend, true, cancellationToken),
        PortableSystemEvent.SystemResumed => capture.SetPauseReasonAsync(PauseReason.SystemSuspend, false, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
