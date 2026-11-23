using System.Diagnostics;
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
    private readonly BraviaPower? _bravia;
    private readonly bool _disconnectWhenDpmsUnavailable;
    private readonly DrmFlipQueue? _flips;
    private readonly DrmCrtcState _savedState;
    private readonly uint[] _savedConnectors;
    private readonly DrmDumbBuffer[] _buffers = new DrmDumbBuffer[2];
    private readonly uint[] _framebuffers = new uint[2];
    private readonly byte[] _compositePixels;
    private readonly bool _streamingScanout;
    private bool _disposed;
    private bool _initialized;

    /// <summary>Index of the buffer currently scanned out (diagnostics).</summary>
    private volatile int _front;

    /// <summary>Whether this output has stopped displaying its desktop.</summary>
    private volatile bool _blanked;
    private bool _crtcDisabled;
    private bool _restoringBravia;
    private bool _braviaRestoreLogged;
    private static readonly TimeSpan PowerRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>The release/acquire handshake with the VT switch signals.</summary>
    private readonly SwitchGate _switchGate = new();

    /// <summary>What each dumb buffer still owes a repaint (see <see cref="BufferAge"/>).</summary>
    private readonly BufferAge _age = new();

    /// <summary>Set when the loop must re-modeset (a failed acquire retry).</summary>
    private volatile bool _remodeset;

    public Scanout(DrmDevice device, DrmOutput output, Scene scene, ScreenPower? screenPower = null, DrmFlipQueue? flips = null)
    {
        _device = device;
        _output = output;
        _scene = scene;
        _screenPower = screenPower;
        _flips = flips;
        _streamingScanout = output.Mode.Area >= 3_000_000 && device.GetVersion().Name == "nvidia-drm";
        _compositePixels = new byte[checked(output.Mode.HDisplay * output.Mode.VDisplay * 4)];
        _savedState = device.GetCrtc(output.CrtcId);
        _savedConnectors = flips is null ? [output.Connector.Id] : SavedConnectors(device, output.CrtcId);
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

        if (screenPower is not null)
        {
            var vendor = MonitorVendor(device, output.Connector);
            _disconnectWhenDpmsUnavailable = vendor == "DEL";
            if (vendor == "SNY")
            {
                _bravia = BraviaPower.FromConfiguration();
                if (_bravia is not null)
                {
                    Console.WriteLine($"rosella: {GnomeConnectorName(output.Connector)} power via BRAVIA Simple IP Control");
                }
            }
        }
    }

    private static uint[] SavedConnectors(DrmDevice device, uint crtc)
    {
        var connectors = new List<uint>();
        foreach (var id in device.GetResources().ConnectorIds)
        {
            try
            {
                var connector = device.GetConnector(id);
                if (connector.EncoderId != 0 && device.GetEncoder(connector.EncoderId).CrtcId == crtc)
                {
                    connectors.Add(id);
                }
            }
            catch (DrmException)
            {
                // A disconnected connector or encoder may have vanished.
            }
        }
        return [.. connectors];
    }

    /// <summary>
    /// Selects a single output for the greeter, using the saved GNOME primary when available.
    /// </summary>
    public static (DrmDevice Device, DrmOutput Output)? Detect()
    {
        var result = DetectDisplays(independentDesktops: false);
        return result is { } detection ? (detection.Device, detection.Outputs[0]) : null;
    }

    internal static (DrmDevice Device, DrmOutput[] Outputs)? DetectDesktops() => DetectDisplays(independentDesktops: true);

    private static (DrmDevice Device, DrmOutput[] Outputs)? DetectDisplays(bool independentDesktops)
    {
        var devices = new List<DrmDevice>();
        var connected = new Dictionary<string, (DrmDevice Device, DrmOutput? Output)>(StringComparer.Ordinal);
        (DrmDevice Device, DrmOutput Output)? fallback = null;
        DrmDevice? selectedDevice = null;
        var ambiguous = false;
        try
        {
            foreach (var path in DrmDevice.CardPaths())
            {
                DrmDevice? device = null;
                try
                {
                    device = DrmDevice.Open(path);
                    devices.Add(device);
                    var resources = device.GetResources();
                    foreach (var connectorId in resources.ConnectorIds)
                    {
                        try
                        {
                            var connector = device.GetConnector(connectorId);
                            if (!connector.IsConnected)
                            {
                                continue;
                            }
                            var output = TrySelectOutput(device, resources, connectorId);
                            if (output is not null && fallback is null)
                            {
                                fallback = (device, output);
                            }
                            if (!connected.TryAdd(GnomeConnectorName(connector), (device, output)))
                            {
                                ambiguous = true;
                            }
                        }
                        catch (DrmException)
                        {
                            // The topology is incomplete, so do not apply a saved configuration.
                            ambiguous = true;
                        }
                    }
                }
                catch (Exception exception) when (exception is DrmException or IOException or UnauthorizedAccessException)
                {
                    // Another card may still be usable, but its topology is unknown.
                    ambiguous = true;
                }
            }

            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configHome) || !Path.IsPathFullyQualified(configHome))
            {
                configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            var primary = ambiguous ? null : GnomeMonitorConfiguration.SelectFile(Path.Combine(configHome, "monitors.xml"), connected);
            if (independentDesktops && TrySelectSonyDell(connected.Values, primary) is { } pair)
            {
                selectedDevice = pair.Device;
                return pair;
            }

            if (primary is not null && connected.TryGetValue(primary.Connector, out var candidate) && candidate.Output is not null)
            {
                var output = candidate.Output with
                {
                    Mode = DrmSelection.SelectMode(candidate.Output.Connector.Modes, primary.Width, primary.Height, primary.Rate, primary.Interlaced),
                };
                selectedDevice = candidate.Device;
                Console.Error.WriteLine($"rosella: GNOME primary {primary.Connector}, mode {output.Mode.HDisplay}x{output.Mode.VDisplay}@{output.Mode.RefreshHz:F3}");
                return (candidate.Device, [output]);
            }

            if (fallback is { } result)
            {
                selectedDevice = result.Device;
                Console.Error.WriteLine($"rosella: no matching GNOME primary; using {GnomeConnectorName(result.Output.Connector)}, mode {result.Output.Mode.HDisplay}x{result.Output.Mode.VDisplay}@{result.Output.Mode.RefreshHz:F3}");
            }
            return fallback is { } single ? (single.Device, [single.Output]) : null;
        }
        finally
        {
            foreach (var device in devices)
            {
                if (device != selectedDevice)
                {
                    device.Dispose();
                }
            }
        }
    }

    private static (DrmDevice Device, DrmOutput[] Outputs)? TrySelectSonyDell(
        IEnumerable<(DrmDevice Device, DrmOutput? Output)> connected, GnomePrimaryMonitor? primary)
    {
        var identified = connected.Where(candidate => candidate.Output is not null)
            .Select(candidate => (candidate.Device, Output: candidate.Output!,
                Vendor: MonitorVendor(candidate.Device, candidate.Output!.Connector))).ToArray();
        var sony = identified.Where(candidate => candidate.Vendor == "SNY").ToArray();
        var dell = identified.Where(candidate => candidate.Vendor == "DEL").ToArray();
        if (sony.Length != 1 || dell.Length != 1 || sony[0].Device != dell[0].Device)
        {
            return null;
        }

        var card = sony[0].Device;
        try
        {
            var resources = card.GetResources();
            var first = sony[0].Output;
            var second = dell[0].Output;
            foreach (var sonyCrtc in CompatibleCrtcs(card, resources, first))
            {
                var dellCrtc = CompatibleCrtcs(card, resources, second).FirstOrDefault(crtc => crtc != sonyCrtc);
                if (dellCrtc != 0)
                {
                    var dellOutput = second with { CrtcId = dellCrtc };
                    dellOutput = primary?.Connector == GnomeConnectorName(dellOutput.Connector)
                        ? WithSavedMode(dellOutput, primary)
                        // Two refresh opportunities per 60 fps video frame, at native resolution.
                        : dellOutput with { Mode = DrmSelection.PreferRefreshRate(dellOutput.Connector.Modes, dellOutput.Mode, 120) };
                    return (card, [WithSavedMode(first with { CrtcId = sonyCrtc }, primary),
                        dellOutput]);
                }
            }
        }
        catch (DrmException exception)
        {
            Console.Error.WriteLine($"rosella: could not assign independent Sony and Dell outputs: {exception.Message}");
        }

        return null;
    }

    private static DrmOutput WithSavedMode(DrmOutput output, GnomePrimaryMonitor? primary) =>
        primary is not null && primary.Connector == GnomeConnectorName(output.Connector)
            ? output with { Mode = DrmSelection.SelectMode(output.Connector.Modes, primary.Width, primary.Height, primary.Rate, primary.Interlaced) }
            : output;

    private static IEnumerable<uint> CompatibleCrtcs(DrmDevice device, DrmResources resources, DrmOutput output)
    {
        yield return output.CrtcId;
        foreach (var encoderId in OrderedEncoderIds(output.Connector))
        {
            var encoder = device.GetEncoder(encoderId);
            for (var i = 0; i < Math.Min(resources.CrtcIds.Count, 32); i++)
            {
                if ((encoder.PossibleCrtcs & (1u << i)) != 0 && resources.CrtcIds[i] != output.CrtcId)
                {
                    yield return resources.CrtcIds[i];
                }
            }
        }
    }

    private static string? MonitorVendor(DrmDevice device, DrmConnector connector)
    {
        var name = connector.Type == DrmConstants.ConnectorTypeDisplayPort ? "DP" : connector.TypeName;
        var path = $"/sys/class/drm/{Path.GetFileName(device.Path)}-{name}-{connector.TypeId}/edid";
        try
        {
            return EdidVendor(File.ReadAllBytes(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? EdidVendor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 128 || !bytes[..8].SequenceEqual(new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }))
        {
            return null;
        }

        var code = (bytes[8] << 8) | bytes[9];
        return new string([(char)('A' - 1 + ((code >> 10) & 31)), (char)('A' - 1 + ((code >> 5) & 31)),
            (char)('A' - 1 + (code & 31))]);
    }

    /// <summary>Mutter connector spelling differs from the kernel for DisplayPort and HDMI-A.</summary>
    internal static string GnomeConnectorName(DrmConnector connector) => connector.Type switch
    {
        0 => $"Unknown-{connector.TypeId}",
        1 => $"VGA-{connector.TypeId}",
        2 => $"DVI-I-{connector.TypeId}",
        4 => $"DVI-A-{connector.TypeId}",
        5 => $"Composite-{connector.TypeId}",
        6 => $"SVIDEO-{connector.TypeId}",
        8 => $"Component-{connector.TypeId}",
        9 => $"DIN-{connector.TypeId}",
        DrmConstants.ConnectorTypeDisplayPort => $"DP-{connector.TypeId}",
        DrmConstants.ConnectorTypeHdmiA or DrmConstants.ConnectorTypeHdmiB => $"HDMI-{connector.TypeId}",
        13 => $"TV-{connector.TypeId}",
        15 => $"Virtual-{connector.TypeId}",
        16 => $"DSI-{connector.TypeId}",
        17 => $"DPI-{connector.TypeId}",
        18 => $"Writeback-{connector.TypeId}",
        19 => $"SPI-{connector.TypeId}",
        20 => $"USB-{connector.TypeId}",
        _ => $"{connector.TypeName}-{connector.TypeId}",
    };

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
    /// face turns its minute over — an idle desktop burns no CPU. Frames
    /// repaint their damage plus any changes the back buffer missed on the
    /// previous flip. While the screen is blanked nothing is
    /// rendered at all: the panel is unlit (DPMS off), so there is no point
    /// drawing into it.
    /// </summary>
    public void Run(CancellationToken token)
    {
        // Read the clock before the first frame: the loop must notice a turn
        // that happens while that frame is being drawn.
        var clock = _scene.Shell?.Taskbar.Module.ClockString() ?? Bar.ClockString();
        Initialize();

        var front = 0;
        long frame = 1;
        var blankState = new BlankState();
        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        Span<DamageRect> repaint = stackalloc DamageRect[DamageTracker.Capacity];

        // Neither buffer has had its damage history initialized yet.
        _age.MarkFull();
        var timing = new FrameTiming(GnomeConnectorName(_output.Connector), _output.Mode.RefreshHz);
        Console.WriteLine("rosella: frame timing enabled");
        while (!token.IsCancellationRequested)
        {
            // Another VT owns the display: stop flipping entirely and let
            // Release() hand the hardware over on its own thread. TryPark
            // latches only while the release is still in force — an acquire
            // that landed first leaves no stale latch for the next release.
            if (_switchGate.IsReleased && _switchGate.TryPark())
            {
                if (!_scene.WaitForDirty(TimeSpan.FromSeconds(30), token))
                {
                    break;
                }

                continue;
            }

            // A failed acquire needs the modeset retried; the device work
            // belongs here, on the loop that owns the device.
            if (_remodeset)
            {
                try
                {
                    SetOutputMode(_framebuffers[_front]);
                    _remodeset = false;
                }
                catch (DrmException exception)
                {
                    if (_restoringBravia)
                    {
                        RetryBraviaScanout(exception, token);
                        continue;
                    }
                    Console.Error.WriteLine($"rosella: re-modeset after VT switch failed: {exception.Message}; retrying");
                }
            }
            // Keep this output in step with its idle policy. The render loop
            // owns DRM changes, and the BRAVIA worker handles its network requests.
            _screenPower?.Tick(_scene.IdleInhibited());
            var wantBlank = _screenPower?.IsBlanked == true;

            // Put up a black frame first, while scanout is still active. The
            // latch keeps it up if the hardware transition needs a retry.
            if (blankState.NeedsBlackFlip(wantBlank))
            {
                ServePendingDiagnostics();
                FlipBlack(token, ref front, ref frame);
            }

            if (wantBlank != _blanked)
            {
                SetBlanked(wantBlank);
                if (!wantBlank && _blanked)
                {
                    token.WaitHandle.WaitOne(PowerRetryDelay);
                    continue;
                }
            }

            var blanked = wantBlank;

            // While the panel is unlit there is nothing to draw: wait for
            // input rather than the clock. Awake, the only thing that changes
            // on its own is the clock face, so the idle bound is the next
            // minute boundary — or the blank policy's own deadline, whichever
            // comes first, so going dark is never late.
            var wait = blanked ? TimeSpan.FromSeconds(30) : IdleWait();
            if (!_scene.WaitForDirty(wait, token))
            {
                break;
            }

            if (blanked)
            {
                // The panel is unlit and the front buffer holds the black
                // frame — a capture serves the screen exactly as it is.
                // This must happen here: leaving a request pending would
                // wedge the control socket's timeout and poison every later
                // capture.
                ServePendingDiagnostics();
                continue;
            }

            ProcessScreenshot(frame);

            // The clock string the bar draws is the one thing that changes
            // without a commit, so its turn is this loop's own damage.
            var clockNow = _scene.Shell?.Taskbar.Module.ClockString() ?? Bar.ClockString();
            if (!string.Equals(clock, clockNow, StringComparison.Ordinal))
            {
                clock = clockNow;
                MarkClockDamage();
            }

            var pieces = _scene.TakeDamage(OutputSize.Width, OutputSize.Height, damage);
            if (pieces == 0)
            {
                // Nothing changed: the wake was the clock bound, a capture or
                // a stale pulse. No paint, no flip, no frame callback.
                ServePendingDiagnostics();
                continue;
            }

            var back = 1 - front;
            var (width, height) = OutputSize;
            var repaintCount = _age.Take(back, damage[..pieces], width, height, repaint);
            if (repaintCount == 0)
            {
                continue;
            }

            ServePendingDiagnostics();
            var renderStarted = Stopwatch.GetTimestamp();
            RenderDamage(back, frame, repaint[..repaintCount]);
            var flipStarted = Stopwatch.GetTimestamp();
            using var wakeDeadline = _restoringBravia ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
            wakeDeadline?.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                var flip = QueueFlip(back, frame);
                // Let clients prepare the next frame while KMS waits for vblank.
                _scene.CompleteFrame();
                WaitForFlip(frame, flip, wakeDeadline?.Token ?? token);
                _restoringBravia = false;
                _braviaRestoreLogged = false;
            }
            catch (Exception exception) when (_restoringBravia && !token.IsCancellationRequested &&
                exception is DrmException or TimeoutException or OperationCanceledException)
            {
                frame++;
                RetryBraviaScanout(exception, token);
                continue;
            }
            var app = _scene.FocusedWindow?.MainSurface.XdgRole?.RootToplevelSurface?.XdgRole?.Toplevel?.AppId;
            timing.Record(renderStarted, flipStarted, app, repaint[..repaintCount]);
            front = back;
            _front = back;
            frame++;
        }
    }

    internal void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        RenderDamage(0, frame: 0, [DamageRect.For(OutputSize.Width, OutputSize.Height)]);
        SetOutputMode(_framebuffers[0]);
        _initialized = true;
    }

    private void SetOutputMode(uint framebuffer)
    {
        _device.SetCrtc(_output.CrtcId, framebuffer, [_output.Connector.Id], _output.Mode);
        // Rosella composites its own pointer. Legacy modesetting can keep
        // the previous session's independent cursor plane enabled.
        try
        {
            _device.ClearHardwareCursor(_output.CrtcId);
        }
        catch (DrmException exception) when (exception.Error is 6 or 38 or 95)
        {
            // This display controller does not expose a legacy cursor.
        }
        catch (DrmException exception)
        {
            Console.Error.WriteLine($"rosella: could not clear the hardware cursor on CRTC {_output.CrtcId}: {exception.Message}");
        }
    }

    /// <summary>Physical output size, from the mode the buffers were made for.</summary>
    private (int Width, int Height) OutputSize => (checked((int)_output.Mode.HDisplay), checked((int)_output.Mode.VDisplay));

    /// <summary>
    /// How long the loop may sleep with nothing to do: the clock face only
    /// changes on a minute boundary. A small bias past the boundary keeps the
    /// wake from landing a millisecond early (and then sleeping another
    /// minute); the clamp keeps a stepped clock, or an arithmetic slip, from
    /// wedging the loop.
    /// </summary>
    internal static TimeSpan NextClockWake(DateTime now)
    {
        var intoMinute = now.Ticks % TimeSpan.TicksPerMinute;
        var remaining = TimeSpan.TicksPerMinute - intoMinute + TimeSpan.FromMilliseconds(20).Ticks;
        return TimeSpan.FromTicks(Math.Clamp(remaining, TimeSpan.FromMilliseconds(50).Ticks, TimeSpan.TicksPerMinute));
    }

    private TimeSpan IdleWait() => IdleWait(DateTime.Now, _screenPower?.UntilIdle, _scene.UntilPointerShrinks, _scene.UntilSpinnerTick);

    /// <summary>
    /// The awake idle bound: the next clock change, shortened to the blank
    /// policy's deadline, the shrinking pointer's deadline and the launch
    /// spinner's next step (both must happen on time even on an idle
    /// desktop), and never so short that the loop spins.
    /// </summary>
    internal static TimeSpan IdleWait(DateTime now, TimeSpan? untilBlank, TimeSpan? untilShrink, TimeSpan? untilSpinner = null)
    {
        var wait = NextClockWake(now);
        if (untilBlank is { } blank && blank < wait)
        {
            wait = blank;
        }

        if (untilShrink is { } shrink && shrink < wait)
        {
            wait = shrink;
        }

        if (untilSpinner is { } spinner && spinner < wait)
        {
            wait = spinner;
        }

        return wait < MinimumWait ? MinimumWait : wait;
    }

    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Damages what a turning clock face can change: the whole taskbar band,
    /// because a wider face (or a locale's longer month) reflows the bar's
    /// own layout. Once a minute, so the band's cost is irrelevant.
    /// </summary>
    private void MarkClockDamage()
    {
        if (_scene.Shell is not { DockVisible: true } shell)
        {
            return;
        }

        if (shell.Settings.IsOpen)
        {
            // A change in dock width also moves the clock's open panel.
            _scene.MarkDirty();
            return;
        }

        var (width, height) = OutputSize;
        var scale = Math.Max(1, _scene.Scale);
        var bandTop = Math.Max(0, ((height / scale) - Shell.BarHeight) * scale);
        _scene.MarkDirty(new DamageRect(0, bandTop, width, height - bandTop));
    }

    /// <summary>
    /// Repaints the regions this back buffer owes. Buffer age accounts for
    /// changes since it last flipped, so we never read from the front buffer.
    /// </summary>
    private void RenderDamage(int back, long frame, ReadOnlySpan<DamageRect> damage)
    {
        var targetBuffer = _buffers[back];
        var target = targetBuffer.Pixels;
        var (width, height) = OutputSize;
        var targetStride = checked((int)targetBuffer.Pitch);
        var compositeStride = width * 4;
        foreach (var region in damage)
        {
            // Translucent layers read the pixels behind them. Composite in
            // cached memory and write finished pixels to the DRM mapping.
            _scene.RenderRegion(_compositePixels, width, height, compositeStride, frame, region);
            Blitter.CopyToScanout(target, width, height, targetStride, _compositePixels, region, _streamingScanout);
        }
    }

    private void ProcessScreenshot(long frame)
    {
        if (_scene.Shell?.Screenshot is not { } screenshot)
        {
            return;
        }

        var (width, height) = OutputSize;
        if (screenshot.SnapshotRequest() is { } request)
        {
            // Snapshot once on the first render wake after the keypress.
            // The selector reads from cached RAM rather than the DRM buffer.
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            _scene.RenderRegion(pixels, width, height, stride, frame, DamageRect.For(width, height), includePointer: false);
            var dimmed = request.Dim ? ScreenshotSelection.Dim(pixels) : null;
            screenshot.SetSnapshot(request.Generation, pixels, dimmed, stride);
        }

        if (screenshot.TakeCapture() is not { } capture)
        {
            return;
        }

        var selection = capture.Region;
        var scale = Math.Max(1, _scene.Scale);
        var scaled = selection.Scaled(scale);
        // Include any last physical row or column on an odd-sized HiDPI mode.
        var right = selection.X + selection.W == _scene.OutputSize.Width ? width : scaled.X + scaled.W;
        var bottom = selection.Y + selection.H == _scene.OutputSize.Height ? height : scaled.Y + scaled.H;
        var region = new DamageRect(scaled.X, scaled.Y, right - scaled.X, bottom - scaled.Y)
            .Intersect(DamageRect.For(width, height));
        if (!region.IsEmpty)
        {
            var original = capture.Frame.Pixels;
            var stride = capture.Frame.Stride;
            _ = Task.Run(() => FrameDump.SaveScreenshot(original, stride, region, png => _scene.Seat?.Clipboard.CopyPng(png)));
        }
    }

    private sealed class FrameTiming(string output, double refreshHz)
    {
        private const int SampleCount = 120;
        private readonly double[] _renders = new double[SampleCount];
        private long _started;
        private long _lastFinished;
        private int _frames;
        private int _overBudget;
        private double _renderTotal;
        private double _presentTotal;
        private double _idleTotal;
        private long _pixels;
        private string? _app;

        internal void Record(long renderStarted, long flipStarted, string? app, ReadOnlySpan<DamageRect> damage)
        {
            var finished = Stopwatch.GetTimestamp();
            var gap = _lastFinished == 0 ? 0 : Stopwatch.GetElapsedTime(_lastFinished, renderStarted).TotalMilliseconds;
            _lastFinished = finished;
            if (!string.Equals(_app, app, StringComparison.Ordinal) || gap > 1000)
            {
                Reset(app);
            }

            if (_frames == 0)
            {
                _started = renderStarted;
            }
            else
            {
                _idleTotal += gap;
            }

            var render = Stopwatch.GetElapsedTime(renderStarted, flipStarted).TotalMilliseconds;
            _renders[_frames++] = render;
            if (refreshHz > 0 && render > 1000 / refreshHz)
            {
                _overBudget++;
            }

            _renderTotal += render;
            _presentTotal += Stopwatch.GetElapsedTime(flipStarted, finished).TotalMilliseconds;
            foreach (var region in damage)
            {
                _pixels += region.Area;
            }

            if (_frames < SampleCount)
            {
                return;
            }

            Array.Sort(_renders);
            var elapsed = Stopwatch.GetElapsedTime(_started, finished).TotalSeconds;
            var label = string.IsNullOrEmpty(app) ? "-" : new string(app.Take(80).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            Console.Error.WriteLine(
                $"rosella: timing {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} output={output}@{refreshHz:F2}Hz app={label} " +
                $"{_frames / elapsed:F1} flips/s, render {_renderTotal / _frames:F2} ms avg / {_renders[113]:F2} p95 / {_renders[^1]:F2} max, " +
                $"over refresh budget {_overBudget}/{_frames}, flip + callbacks {_presentTotal / _frames:F2} ms avg, " +
                $"between frames {_idleTotal / (_frames - 1):F2} ms avg, {_pixels / _frames:N0} pixels repainted/flip");
            Reset(app);
        }

        private void Reset(string? app)
        {
            _app = app;
            _frames = _overBudget = 0;
            _pixels = 0;
            _renderTotal = _presentTotal = _idleTotal = 0;
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

        // Wake the loop without inventing damage: a capture reads the front
        // buffer, so nothing needs repainting.
        _scene.MarkDirty(DamageRect.Empty);
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

    /// <summary>
    /// Stops this output's loop before the VT handover restores the card.
    /// </summary>
    internal void Park()
    {
        if (!_switchGate.BeginRelease(TimeSpan.FromSeconds(2), _scene.MarkDirty))
        {
            // A wedged loop still must not hold the hardware hostage.
            Console.Error.WriteLine("rosella: render loop did not park for VT release; taking the device anyway");
            return;
        }

        if (_blanked)
        {
            SetBlanked(false);
        }
    }

    /// <summary>
    /// Re-modesets the output once the VT handover takes DRM master back.
    /// </summary>
    internal void Resume()
    {
        _screenPower?.MarkActivity();
        try
        {
            SetOutputMode(_framebuffers[_front]);
        }
        catch (DrmException exception)
        {
            Console.Error.WriteLine($"rosella: re-modeset on VT acquire failed: {exception.Message}; the render loop will retry");
            _remodeset = true;
        }

        _switchGate.EndRelease();
        _scene.MarkDirty();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Wake outputs before restoring the previous display configuration.
        for (var attempt = 0; attempt < 3 && _blanked; attempt++)
        {
            SetBlanked(false);
        }

        _bravia?.Dispose();
        Restore();

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

    internal void Restore()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            if (_savedConnectors.Length != 0 && _savedState is { ModeValid: true, Mode: { } mode })
            {
                _device.SetCrtc(_savedState.Id, _savedState.FramebufferId, _savedConnectors, mode, _savedState.X, _savedState.Y);
            }
            else
            {
                _device.DisableCrtc(_output.CrtcId);
            }
        }
        catch (DrmException)
        {
            // The next session will re-modeset when its framebuffer is ready.
        }
    }

    /// <summary>
    /// Flips to a black frame: the floor under DPMS in the blank state. A
    /// panel that cannot be powered down must not freeze the last bright
    /// frame (burn-in), so the screen shows nothing while blanked, lit or
    /// not. Runs on the render loop, before the panel is undriven so the
    /// flip happens while scanout is still active; a rejected or stalling
    /// flip degrades to DPMS-only instead of killing the session.
    /// </summary>
    private void FlipBlack(CancellationToken token, ref int front, ref long frame)
    {
        var back = 1 - front;
        _buffers[back].Pixels.Clear();
        _age.MarkFull();

        Task? flip;
        try
        {
            flip = QueueFlip(back, frame);
        }
        catch (DrmException exception)
        {
            Console.Error.WriteLine($"rosella: blank flip failed: {exception.Message}; continuing on DPMS alone");
            frame++;
            return;
        }

        // Bounded: a driver that stalls scanout must not wedge the blank.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            WaitForFlip(frame, flip, deadline.Token);
        }
        catch (Exception exception) when (!token.IsCancellationRequested && exception is OperationCanceledException or TimeoutException)
        {
            // The flip was accepted; only its event never came. It will
            // land (or the panel is already unlit) — move on either way.
            Console.Error.WriteLine("rosella: blank flip event timed out; continuing on DPMS alone");
        }

        front = back;
        _front = back;
        frame++;
    }

    /// <summary>
    /// Drives the connector's power or hands the request to the BRAVIA worker.
    /// DRM failures leave the previous state in place so the loop can retry.
    /// </summary>
    private void SetBlanked(bool blank)
    {
        if (_bravia is not null)
        {
            _bravia.SetBlanked(blank);
            _restoringBravia = !blank;
            _blanked = blank;
            if (!blank)
            {
                _scene.MarkDirty();
            }
            return;
        }

        try
        {
            if (_crtcDisabled)
            {
                SetOutputMode(_framebuffers[_front]);
                _crtcDisabled = false;
            }
            else if (!_device.SetDpms(_output.Connector.Id, !blank))
            {
                if (blank && _disconnectWhenDpmsUnavailable)
                {
                    _device.DisableCrtc(_output.CrtcId);
                    _crtcDisabled = true;
                }
                else if (!_dpmsMissingLogged)
                {
                    _dpmsMissingLogged = true;
                    Console.Error.WriteLine($"rosella: {GnomeConnectorName(_output.Connector)} has no DPMS property");
                }
            }
        }
        catch (DrmException exception)
        {
            Console.Error.WriteLine($"rosella: DPMS {(blank ? "off" : "on")} failed: {exception.Message}; retrying");
            return;
        }

        _blanked = blank;
        Console.Error.WriteLine($"rosella: {GnomeConnectorName(_output.Connector)} {(blank ? "blanked" : "awake")}");

        // The panel is back on: show something immediately rather than
        // waiting for the next scene change.
        if (!blank)
        {
            _scene.MarkDirty();
        }
    }

    private bool _dpmsMissingLogged;

    private void RetryBraviaScanout(Exception exception, CancellationToken token)
    {
        if (!_braviaRestoreLogged)
        {
            Console.Error.WriteLine($"rosella: waiting for BRAVIA HDMI to return: {exception.Message}");
            _braviaRestoreLogged = true;
        }
        _remodeset = true;
        _age.MarkFull();
        _scene.MarkDirty();
        token.WaitHandle.WaitOne(PowerRetryDelay);
    }

    private ulong FlipCookie(long frame) => ((ulong)_output.CrtcId << 32) | (uint)frame;

    private Task? QueueFlip(int buffer, long frame)
    {
        var cookie = FlipCookie(frame);
        if (_flips is not null)
        {
            return _flips.Submit(_output.CrtcId, _framebuffers[buffer], cookie);
        }

        _device.PageFlip(_output.CrtcId, _framebuffers[buffer], cookie);
        return null;
    }

    private void WaitForFlip(long frame, Task? completion, CancellationToken token)
    {
        var userData = FlipCookie(frame);
        if (completion is not null)
        {
            try
            {
                completion.WaitAsync(TimeSpan.FromSeconds(2), token).GetAwaiter().GetResult();
            }
            finally
            {
                _flips!.Forget(userData);
            }
            return;
        }

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
