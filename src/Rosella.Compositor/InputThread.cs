using System.Net.Sockets;
using Rosella.Input;

namespace Rosella.Compositor;

/// <summary>
/// Discovers evdev devices synchronously (before clients bind, so seat
/// capabilities are right from the start), grabs them exclusively, and runs
/// one reader thread per device feeding the router. A watcher thread polls
/// for hot-plugged devices (mice plugged in mid-session): new devices get
/// the same open/classify/grab treatment and the seat re-announces its
/// capabilities; unplugged devices end their reader, leave the seat, and
/// their path is forgotten so a replug works. While the session is parked
/// on another VT, hot-plugged devices join ungrabbed — the console owns
/// input until Resume() grabs them with the rest.
/// </summary>
internal sealed class InputThread : IDisposable
{
    private sealed class DeviceEntry(IInputDevice device, string path, bool isKeyboard, bool isPointer)
    {
        public IInputDevice Device { get; } = device;

        public string Path { get; } = path;

        public bool IsKeyboard { get; } = isKeyboard;

        public bool IsPointer { get; } = isPointer;

        /// <summary>Whether the exclusive grab is held. Guarded by the device gate.</summary>
        public bool Grabbed { get; set; }
    }

    private readonly object _devicesGate = new();
    private readonly SeatState _seat;
    private readonly InputRouter _router;
    private readonly IDeviceSource _source;
    private readonly List<DeviceEntry> _devices = new();
    private readonly HashSet<string> _knownPaths = new();
    private readonly List<Thread> _threads = new();
    private Thread? _watchThread;
    private volatile bool _disposed;
    private volatile bool _started;
    private bool _suspended; // guarded by _devicesGate
    private int _keyboardCount;
    private int _pointerCount;

    internal InputThread(Scene scene, SeatState seat, Action quit, Shell? shell = null, ScreenPower? screenPower = null, Action<int>? switchVt = null, IDeviceSource? source = null)
    {
        _seat = seat;
        _source = source ?? new EvdevDeviceSource();
        _router = new InputRouter(scene, seat, quit, shell, screenPower, switchVt);
    }

    internal InputRouter Router => _router;

    /// <summary>
    /// VT switch away: release the exclusive grabs so the console or the
    /// other session receives input while we are parked. Events already in
    /// flight are discarded (the router ignores input while suspended).
    /// </summary>
    internal void Suspend()
    {
        _router.Suspended = true;
        _router.ResetInputState();
        lock (_devicesGate)
        {
            _suspended = true;
            foreach (var entry in _devices)
            {
                try
                {
                    entry.Device.Ungrab();
                    entry.Grabbed = false;
                }
                catch (IOException)
                {
                    // Transient (the switch signals race these ioctls) or
                    // the device is gone. Grabbed stays set, and the grab
                    // reconciliation keeps re-trying: the console must
                    // never stay starved of input by one failed ioctl.
                }
            }
        }
    }

    /// <summary>VT switch back: re-grab every device and route input again.</summary>
    internal void Resume()
    {
        List<string>? log = null;
        lock (_devicesGate)
        {
            _suspended = false;
            foreach (var entry in _devices)
            {
                if (entry.Grabbed)
                {
                    continue; // still held from before (its ungrab failed)
                }

                try
                {
                    entry.Device.Grab();
                    entry.Grabbed = true;
                }
                catch (IOException exception)
                {
                    (log ??= []).Add($"rosella: input device '{entry.Device.Name}' re-grab failed (will retry): {exception.Message}");
                }
            }
        }

        if (log is not null)
        {
            foreach (var line in log)
            {
                Console.Error.WriteLine(line);
            }
        }

        _router.Suspended = false;
    }

    /// <summary>Opens, classifies, and grabs devices (no reader threads yet). Safe to call from tests.</summary>
    internal void Discover()
    {
        foreach (var path in _source.EnumeratePaths())
        {
            TryAddDevice(path);
        }
    }

    /// <summary>Starts one reader thread per discovered device, plus the hot-plug watcher.</summary>
    internal void Start()
    {
        lock (_devicesGate)
        {
            _started = true;
            foreach (var entry in _devices)
            {
                StartReader(entry);
            }
        }

        _watchThread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "rosella-input-hotplug",
        };
        _watchThread.Start();
    }

    public void Dispose()
    {
        _disposed = true;

        List<DeviceEntry> devices;
        List<Thread> threads;
        lock (_devicesGate)
        {
            devices = [.. _devices];
            _devices.Clear();
            threads = [.. _threads];
            _threads.Clear();
        }

        foreach (var entry in devices)
        {
            entry.Device.Dispose();
        }

        foreach (var thread in threads)
        {
            thread.Join(1000);
        }

        _watchThread?.Join(1000);
    }

    /// <summary>
    /// Opens one device and adds it to the seat; failure stays local and the
    /// path is left unknown, so the watcher retries on a later poll (udev
    /// permissions often lag the node's appearance at hot-plug).
    /// </summary>
    private void TryAddDevice(string path)
    {
        if (_source.TryOpen(path) is not { } device)
        {
            Console.Error.WriteLine($"rosella: input: {path} not opened (permission or busy)");
            return;
        }

        if (!_source.IsKeyboard(device) && !_source.IsPointer(device))
        {
            Console.Error.WriteLine($"rosella: input: {device.Name} skipped (not keyboard or pointer)");
            device.Dispose();
            lock (_devicesGate)
            {
                _knownPaths.Add(path); // permanently not ours: never retry
            }

            return;
        }

        var isKeyboard = _source.IsKeyboard(device);
        var isPointer = _source.IsPointer(device);
        var tag = (isKeyboard, isPointer) switch
        {
            (true, true) => "keyboard+pointer",
            (true, false) => "keyboard",
            (false, true) => "pointer",
            _ => string.Empty,
        };

        var capabilitiesChanged = false;
        string? logLine = null;
        var added = false;
        lock (_devicesGate)
        {
            if (!_disposed)
            {
                var grabbed = false;
                if (_suspended)
                {
                    // Parked on another VT: the console owns input. Join
                    // ungrabbed and let Resume() grab this device with the rest.
                    logLine = $"rosella: input: {device.Name} added (grab deferred until resume) ({tag})";
                }
                else
                {
                    try
                    {
                        device.Grab();
                        grabbed = true;
                        logLine = $"rosella: input: {device.Name} grabbed ({tag})";
                    }
                    catch (IOException exception)
                    {
                        // EBUSY just means another compositor owns the node
                        // (e.g. GNOME on another VT). Stay open with the grab
                        // unreconciled — the sweep retries the single ioctl —
                        // instead of re-opening the node every second.
                        logLine = $"rosella: input device '{device.Name}' not grabbed yet: {exception.Message}";
                    }
                }

                added = true;
                _knownPaths.Add(path);
                _devices.Add(new DeviceEntry(device, path, isKeyboard, isPointer) { Grabbed = grabbed });
                if (isKeyboard)
                {
                    _keyboardCount++;
                    if (!_seat.HasKeyboardDevice)
                    {
                        _seat.HasKeyboardDevice = true;
                        capabilitiesChanged = true;
                    }
                }

                if (isPointer)
                {
                    _pointerCount++;
                    if (!_seat.HasPointerDevice)
                    {
                        _seat.HasPointerDevice = true;
                        capabilitiesChanged = true;
                    }
                }

                if (_started)
                {
                    StartReader(_devices[^1]);
                }
            }
        }

        if (!added)
        {
            // Disposed mid-add: never leave a grabbed device behind.
            device.Dispose();
        }

        if (logLine is not null)
        {
            // Outside the gate: stderr must never stall input or switching.
            Console.Error.WriteLine(logLine);
        }

        if (capabilitiesChanged)
        {
            _seat.NotifyCapabilitiesChanged();
        }
    }

    /// <summary>Must hold the device gate. Reader threads end at unplug or dispose.</summary>
    private void StartReader(DeviceEntry entry)
    {
        var captured = entry;
        var thread = new Thread(() => ReadLoop(captured))
        {
            IsBackground = true,
            Name = $"rosella-input-{captured.Device.Name}",
        };
        thread.Start();
        _threads.Add(thread);
    }

    /// <summary>
    /// Reconciles grabs toward the session's current target: parked means
    /// every device ends up released to the console; active means every
    /// device ends up grabbed. A failed grab or ungrab (transient error,
    /// another compositor holding the node) is re-tried on each sweep until
    /// it sticks, so one bad ioctl can never starve the console or strand a
    /// device. Success is logged; silent failure was logged at its origin.
    /// </summary>
    private void ReconcileGrabs()
    {
        List<string>? log = null;
        lock (_devicesGate)
        {
            var target = !_suspended;
            foreach (var entry in _devices)
            {
                if (entry.Grabbed == target)
                {
                    continue;
                }

                try
                {
                    if (target)
                    {
                        entry.Device.Grab();
                    }
                    else
                    {
                        entry.Device.Ungrab();
                    }

                    entry.Grabbed = target;
                    (log ??= []).Add($"rosella: input: {entry.Device.Name} {(target ? "grabbed" : "released")} (after retry)");
                }
                catch (IOException)
                {
                    // Still failing; the next sweep tries again.
                }
            }
        }

        if (log is not null)
        {
            foreach (var line in log)
            {
                Console.Error.WriteLine(line);
            }
        }
    }

    /// <summary>One hot-plug sweep: opens and adds anything new. Safe to call from tests.</summary>
    internal void PollHotPlug()
    {
        ReconcileGrabs();
        foreach (var path in _source.EnumeratePaths())
        {
            bool known;
            lock (_devicesGate)
            {
                known = _knownPaths.Contains(path);
            }

            if (!known)
            {
                Console.Error.WriteLine($"rosella: input: hot-plug {path}");
                TryAddDevice(path);
            }
        }
    }

    /// <summary>Polls for hot-plugged devices; the device set is never frozen.</summary>
    private void WatchLoop()
    {
        while (!_disposed)
        {
            Thread.Sleep(1000);
            try
            {
                PollHotPlug();
            }
            catch (IOException)
            {
                // /dev/input raced; the next poll sees it.
            }
        }
    }

    /// <summary>An unplugged device leaves the seat (and may change capabilities).</summary>
    private void RemoveDevice(DeviceEntry entry)
    {
        var capabilitiesChanged = false;
        lock (_devicesGate)
        {
            if (!_devices.Remove(entry))
            {
                // Dispose() already took it; nothing to unwind.
                return;
            }

            // Forget the path: a replug of the same evdev node must work.
            _knownPaths.Remove(entry.Path);
            if (entry.IsKeyboard && --_keyboardCount == 0)
            {
                _seat.HasKeyboardDevice = false;
                capabilitiesChanged = true;
            }

            if (entry.IsPointer && --_pointerCount == 0)
            {
                _seat.HasPointerDevice = false;
                capabilitiesChanged = true;
            }
        }

        entry.Device.Dispose();
        if (capabilitiesChanged)
        {
            _seat.NotifyCapabilitiesChanged();
        }
    }

    private void ReadLoop(DeviceEntry entry)
    {
        try
        {
            while (true)
            {
                var e = entry.Device.ReadEvent();
                try
                {
                    _router.ProcessEvent(e);
                }
                catch (SocketException)
                {
                    // A client vanished mid-send; its session teardown owns
                    // that. The device reader continues.
                }
            }
        }
        catch (IOException)
        {
            // Device unplugged or disposed: the reader ends.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown during a read.
        }

        if (!_disposed)
        {
            RemoveDevice(entry);
        }
    }
}
