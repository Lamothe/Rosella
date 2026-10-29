using Rosella.Drm;

namespace Rosella.Compositor;

/// <summary>
/// Drives one output: two dumb buffers, software-rendered test pattern, and a
/// page-flip loop. Restores the previous CRTC state on disposal.
/// </summary>
internal sealed class Scanout : IDisposable
{
    private const uint BitsPerPixel = 32;
    private const uint Depth = 24;

    private readonly DrmDevice _device;
    private readonly DrmOutput _output;
    private readonly Scene _scene;
    private readonly ScreenPower? _screenPower;
    private readonly DrmCrtcState _savedState;
    private readonly DrmDumbBuffer[] _buffers = new DrmDumbBuffer[2];
    private readonly uint[] _framebuffers = new uint[2];
    private bool _disposed;

    /// <summary>Index of the buffer currently scanned out (diagnostics).</summary>
    private volatile int _front;

    /// <summary>Whether the panel is currently unlit (DPMS off).</summary>
    private volatile bool _blanked;

    public Scanout(DrmDevice device, DrmOutput output, Scene scene, ScreenPower? screenPower = null)
    {
        _device = device;
        _output = output;
        _scene = scene;
        _screenPower = screenPower;
        _savedState = device.GetCrtc(output.CrtcId);
        try
        {
            for (var i = 0; i < 2; i++)
            {
                _buffers[i] = device.CreateDumb(output.Mode.HDisplay, output.Mode.VDisplay, BitsPerPixel);
                _framebuffers[i] = device.AddFramebuffer(
                    output.Mode.HDisplay,
                    output.Mode.VDisplay,
                    _buffers[i].Pitch,
                    BitsPerPixel,
                    Depth,
                    _buffers[i].Handle);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Finds the first connected connector on the first card that has one,
    /// and works out its CRTC and mode. Opinionated: no configuration.
    /// </summary>
    public static (DrmDevice Device, DrmOutput Output)? Detect()
    {
        foreach (var path in DrmDevice.CardPaths())
        {
            DrmDevice? device = null;
            try
            {
                device = DrmDevice.Open(path);
                var resources = device.GetResources();
                foreach (var connectorId in resources.ConnectorIds)
                {
                    var output = TrySelectOutput(device, resources, connectorId);
                    if (output is not null)
                    {
                        return (device, output);
                    }
                }

                device.Dispose();
            }
            catch (DrmException)
            {
                device?.Dispose();
            }
        }

        return null;
    }

    /// <summary>One connector's selection attempt; failures stay local to the connector.</summary>
    private static DrmOutput? TrySelectOutput(DrmDevice device, DrmResources resources, uint connectorId)
    {
        try
        {
            var connector = device.GetConnector(connectorId);
            if (!connector.IsConnected || connector.Modes.Count == 0 || connector.EncoderIds.Count == 0)
            {
                return null;
            }

            var mode = DrmSelection.SelectMode(connector.Modes);
            foreach (var encoderId in OrderedEncoderIds(connector))
            {
                try
                {
                    var encoder = device.GetEncoder(encoderId);
                    var crtcId = DrmSelection.SelectCrtc(resources.CrtcIds, encoder);
                    return new DrmOutput(connector, crtcId, mode);
                }
                catch (DrmException)
                {
                    // Try the connector's next encoder.
                }
                catch (InvalidOperationException)
                {
                    // Encoder/CRTC pairing did not work out; try the next encoder.
                }
            }
        }
        catch (DrmException)
        {
            // Connector query failed; skip it and keep the card's other connectors.
        }

        return null;
    }

    /// <summary>The connector's current encoder first, then the rest.</summary>
    private static IEnumerable<uint> OrderedEncoderIds(DrmConnector connector)
    {
        if (connector.EncoderId != 0)
        {
            yield return connector.EncoderId;
        }

        foreach (var encoderId in connector.EncoderIds)
        {
            if (encoderId != connector.EncoderId)
            {
                yield return encoderId;
            }
        }
    }

    /// <summary>
    /// Runs the flip loop until the token is cancelled. Composites only when
    /// the scene is dirty (commits, cursor moves, shell state) or the clock
    /// tick bound expires — an idle desktop burns no CPU. While the screen is
    /// blanked nothing is rendered at all: the panel is unlit (DPMS off), so
    /// there is no point drawing into it.
    /// </summary>
    public void Run(CancellationToken token)
    {
        RenderInto(0, frame: 0);
        _device.SetCrtc(_output.CrtcId, _framebuffers[0], [_output.Connector.Id], _output.Mode);

        var front = 0;
        long frame = 1;
        while (!token.IsCancellationRequested)
        {
            // Sync the panel's power state with the policy. The policy flips
            // to blanked on its own when idle, and back on the input thread's
            // MarkActivity; the hardware change happens only here, on the one
            // thread that owns the DRM device. SetBlanked commits state only
            // on success, so a failed ioctl is retried on the next turn
            // instead of leaving the two out of step.
            _screenPower?.Tick();
            var wantBlank = _screenPower?.IsBlanked == true;
            if (wantBlank != _blanked)
            {
                SetBlanked(wantBlank);
            }

            var blanked = _blanked;

            // While the panel is unlit there is nothing to draw: wait for
            // input rather than the one-second clock bound.
            var wait = blanked ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(1);
            if (!_scene.WaitForDirty(wait, token))
            {
                break;
            }

            if (blanked)
            {
                // The panel is unlit but the front buffer still holds the
                // last drawn frame, so a capture can be served from it. This
                // must happen here: leaving a request pending would wedge the
                // control socket's timeout and poison every later capture.
                ServePendingDiagnostics();
                continue;
            }

            var back = 1 - front;
            ServePendingDiagnostics();
            RenderInto(back, frame);
            _device.PageFlip(_output.CrtcId, _framebuffers[back], (ulong)frame);
            WaitForFlip((ulong)frame, token);
            _scene.CompleteFrame();
            front = back;
            _front = back;
            frame++;
        }
    }

    private volatile bool _dumpRequested;
    private readonly object _captureGate = new();
    private TaskCompletionSource<byte[]>? _capture;
    private bool _fileDumpPending;

    /// <summary>
    /// Serves whatever diagnostics are pending against the front buffer: a
    /// control-socket capture gets the frame as bytes, the signal path writes
    /// the file. Both may be pending in the same wake; both are served. Runs
    /// on the render loop (which owns the buffers), so it must never be
    /// called from anywhere else.
    /// </summary>
    private void ServePendingDiagnostics()
    {
        if (!_dumpRequested)
        {
            return;
        }

        TaskCompletionSource<byte[]>? capture;
        bool fileDump;
        lock (_captureGate)
        {
            capture = _capture;
            _capture = null;
            fileDump = _fileDumpPending;
            _fileDumpPending = false;
            _dumpRequested = false;
        }

        if (capture != null)
        {
            var frontBuffer = _buffers[_front];
            capture.TrySetResult(FrameDump.EncodePpm(
                _output.Mode.HDisplay, _output.Mode.VDisplay, frontBuffer.Pitch, frontBuffer.Pixels));
        }

        if (fileDump)
        {
            DumpFrame("/tmp/rosella-frame.ppm");
        }
    }

    /// <summary>
    /// Requests a diagnostics dump of the on-screen frame. Signal-safe:
    /// only sets flags — the render loop performs the write on its next
    /// wake (the buffers are owned by the loop; writing from a signal
    /// thread races it).
    /// </summary>
    internal void RequestDump()
    {
        lock (_captureGate)
        {
            _fileDumpPending = true;
            _dumpRequested = true;
        }
    }

    /// <summary>
    /// Captures the on-screen frame as PPM bytes. Handshakes with the
    /// render loop (which owns the buffers): wakes the loop and completes
    /// when the loop has encoded the frame. Only one capture may be in
    /// flight; concurrent callers get a clear error instead of a lost
    /// request. Blocks the caller — never call from the render loop.
    /// </summary>
    internal Task<byte[]> CaptureFrame()
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_captureGate)
        {
            if (_capture is not null)
            {
                throw new InvalidOperationException("a frame capture is already in flight");
            }

            _capture = tcs;
            _dumpRequested = true;
        }

        _scene.MarkDirty();
        return tcs.Task;
    }

    /// <summary>Called on shutdown: a pending capture must not outlive the loop.</summary>
    internal void CancelPendingCapture()
    {
        lock (_captureGate)
        {
            _capture?.TrySetCanceled();
            _capture = null;
        }
    }

    /// <summary>
    /// Dumps what is on screen right now to a PPM file (diagnostics: the
    /// ground truth for visual reports — may tear mid-flip, which is fine
    /// for its purpose). Must be called from the render loop thread.
    /// </summary>
    internal void DumpFrame(string path)
    {
        var buffer = _buffers[_front];
        FrameDump.WritePpm(path, _output.Mode.HDisplay, _output.Mode.VDisplay, buffer.Pitch, buffer.Pixels);
        Console.Error.WriteLine($"rosella: frame dumped to {path}");
        foreach (var line in _scene.DescribeSurfaces())
        {
            Console.Error.WriteLine($"rosella: {line}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // A blanked panel must be woken before anything else, or the person
        // logging back in sees a dead screen. A failed wake is retried: the
        // CRTC restore below may bring the display back regardless, but DPMS
        // state is per connector and must be undone here.
        for (var attempt = 0; attempt < 3 && _blanked; attempt++)
        {
            SetBlanked(false);
        }

        // Restore what the previous session had, if the kernel still accepts it.
        if (_savedState is { ModeValid: true, Mode: not null })
        {
            try
            {
                _device.SetCrtc(_savedState.Id, _savedState.FramebufferId, [_output.Connector.Id], _savedState.Mode.Value);
            }
            catch (DrmException)
            {
                // The old framebuffer is gone; the next session will re-modeset anyway.
            }
        }

        for (var i = 0; i < 2; i++)
        {
            if (_framebuffers[i] != 0)
            {
                try
                {
                    _device.RemoveFramebuffer(_framebuffers[i]);
                }
                catch (DrmException)
                {
                    // Already gone.
                }
            }

            _buffers[i]?.Dispose();
        }
    }

    private void RenderInto(int bufferIndex, long frame)
    {
        var buffer = _buffers[bufferIndex];
        _scene.Render(buffer.Pixels, _output.Mode.HDisplay, _output.Mode.VDisplay, checked((int)buffer.Pitch), frame);
    }

    /// <summary>
    /// Drives the panel's real power state through DPMS. A driver without a
    /// DPMS property simply keeps the panel lit (reported once); either way
    /// compositing stops while blanked, so the CPU stays idle. State is only
    /// committed on success — a failed ioctl leaves the previous state in
    /// place, so the render loop's next turn retries rather than believing a
    /// panel is lit when it is not. Must run on the render loop thread.
    /// </summary>
    private void SetBlanked(bool blank)
    {
        try
        {
            if (!_device.SetDpms(_output.Connector.Id, !blank))
            {
                if (!_dpmsMissingLogged)
                {
                    _dpmsMissingLogged = true;
                    Console.Error.WriteLine("rosella: connector has no DPMS property; screen cannot be powered down");
                }

                // Nothing to drive, but stop compositing all the same.
                _blanked = blank;
                _scene.MarkDirty();
                return;
            }
        }
        catch (DrmException exception)
        {
            Console.Error.WriteLine($"rosella: DPMS {(blank ? "off" : "on")} failed: {exception.Message}; retrying");
            return;
        }

        _blanked = blank;
        Console.Error.WriteLine($"rosella: screen {(blank ? "blanked" : "awake")}");

        // The panel is back on: show something immediately rather than
        // waiting for the next scene change.
        if (!blank)
        {
            _scene.MarkDirty();
        }
    }

    private bool _dpmsMissingLogged;

    private void WaitForFlip(ulong userData, CancellationToken token)
    {
        while (true)
        {
            foreach (var flipEvent in _device.ReadEvents(token))
            {
                if (IsTargetFlip(flipEvent, userData))
                {
                    return;
                }
            }
        }
    }

    /// <summary>Whether an event is the completion of our flip, ignoring stale ones.</summary>
    internal static bool IsTargetFlip(DrmFlipEvent flipEvent, ulong userData)
    {
        return flipEvent.Type == DrmConstants.EventFlipComplete && flipEvent.UserData == userData;
    }
}
