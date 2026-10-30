namespace Rosella.Compositor;

/// <summary>
/// The release/acquire handshake between a VT switch signal and the render
/// loop. Release() runs on a signal thread and must not touch the DRM
/// device until the loop has stepped away from it; the loop latches
/// "parked" only while the release is still in force, so an acquire that
/// lands early can never leave a stale latch behind for the next release
/// to race against.
/// </summary>
internal sealed class SwitchGate
{
    private readonly object _gate = new();
    private bool _released;
    private bool _parked;

    /// <summary>Another VT now owns the display; the loop must park.</summary>
    internal bool IsReleased
    {
        get
        {
            lock (_gate)
            {
                return _released;
            }
        }
    }

    /// <summary>
    /// The loop's turn at the handshake: latches "parked" and returns true
    /// when a release is in force. Returns false — and touches nothing —
    /// when an acquire has already landed.
    /// </summary>
    internal bool TryPark()
    {
        lock (_gate)
        {
            if (!_released)
            {
                return false;
            }

            _parked = true;
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    /// <summary>
    /// Declares a release in force and waits (bounded) for the loop to park.
    /// A timeout does not abandon the release — it stays in force so the
    /// loop parks at its next opportunity — and only EndRelease clears it.
    /// </summary>
    internal bool BeginRelease(TimeSpan parkTimeout)
    {
        var deadline = DateTime.UtcNow + parkTimeout;
        lock (_gate)
        {
            _released = true;
            _parked = false;
            while (!_parked)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_gate, remaining))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>The device is handed back; the loop may flip again on its next wake.</summary>
    internal void EndRelease()
    {
        lock (_gate)
        {
            _released = false;
            _parked = false;
            Monitor.PulseAll(_gate);
        }
    }
}
