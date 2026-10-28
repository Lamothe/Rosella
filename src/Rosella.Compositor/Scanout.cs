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
    private readonly DrmCrtcState _savedState;
    private readonly DrmDumbBuffer[] _buffers = new DrmDumbBuffer[2];
    private readonly uint[] _framebuffers = new uint[2];
    private bool _disposed;

    /// <summary>Index of the buffer currently scanned out (diagnostics).</summary>
    private volatile int _front;

    public Scanout(DrmDevice device, DrmOutput output, Scene scene)
    {
        _device = device;
        _output = output;
        _scene = scene;
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
    /// tick bound expires — an idle desktop burns no CPU.
    /// </summary>
    public void Run(CancellationToken token)
    {
        RenderInto(0, frame: 0);
        _device.SetCrtc(_output.CrtcId, _framebuffers[0], [_output.Connector.Id], _output.Mode);

        var front = 0;
        long frame = 1;
        while (!token.IsCancellationRequested)
        {
            if (!_scene.WaitForDirty(TimeSpan.FromSeconds(1), token))
            {
                break;
            }

            var back = 1 - front;

            if (_dumpRequested)
            {
                _dumpRequested = false;
                DumpFrame("/tmp/rosella-frame.ppm");
            }

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

    /// <summary>
    /// Requests a diagnostics dump of the on-screen frame. Signal-safe:
    /// only sets a flag — the render loop performs the write on its next
    /// wake (the buffers are owned by the loop; writing from a signal
    /// thread races it).
    /// </summary>
    internal void RequestDump() => _dumpRequested = true;

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
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

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
