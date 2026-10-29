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
/// their path is forgotten so a replug works.
/// </summary>
internal sealed class InputThread : IDisposable
{
    private sealed record DeviceEntry(EvdevDevice Device, string Path, bool IsKeyboard, bool IsPointer);

    private readonly object _devicesGate = new();
    private readonly SeatState _seat;
    private readonly InputRouter _router;
    private readonly List<DeviceEntry> _devices = new();
    private readonly HashSet<string> _knownPaths = new();
    private readonly List<Thread> _threads = new();
    private Thread? _watchThread;
    private volatile bool _disposed;
    private volatile bool _started;
    private int _keyboardCount;
    private int _pointerCount;

    internal InputThread(Scene scene, SeatState seat, Action quit, Shell? shell = null, ScreenPower? screenPower = null)
    {
        _seat = seat;
        _router = new InputRouter(scene, seat, quit, shell, screenPower);
    }

    internal InputRouter Router => _router;

    /// <summary>Opens, classifies, and grabs devices (no reader threads yet). Safe to call from tests.</summary>
    internal void Discover()
    {
        foreach (var path in DeviceDiscovery.EnumeratePaths())
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
        if (EvdevDevice.TryOpen(path) is not { } device)
        {
            Console.Error.WriteLine($"rosella: input: {path} not opened (permission or busy)");
            return;
        }

        if (!DeviceDiscovery.IsKeyboard(device) && !DeviceDiscovery.IsPointer(device))
        {
            Console.Error.WriteLine($"rosella: input: {device.Name} skipped (not keyboard or pointer)");
            device.Dispose();
            lock (_devicesGate)
            {
                _knownPaths.Add(path); // permanently not ours: never retry
            }

            return;
        }

        var isKeyboard = DeviceDiscovery.IsKeyboard(device);
        var isPointer = DeviceDiscovery.IsPointer(device);
        try
        {
            // EBUSY here just means another compositor owns the device
            // (e.g. GNOME on another VT): skip it rather than die.
            device.Grab();
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"rosella: input device '{device.Name}' not grabbed: {exception.Message}");
            device.Dispose();
            return;
        }

        var capabilitiesChanged = false;
        lock (_devicesGate)
        {
            if (_disposed)
            {
                // Disposed mid-add: never leave a grabbed device behind.
                device.Dispose();
                return;
            }

            Console.Error.WriteLine($"rosella: input: {device.Name} grabbed ({(isKeyboard ? "keyboard" : "")}{(isPointer ? "pointer" : "")})");
            _knownPaths.Add(path);
            _devices.Add(new DeviceEntry(device, path, isKeyboard, isPointer));
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

    /// <summary>Polls for hot-plugged devices; the device set is never frozen.</summary>
    private void WatchLoop()
    {
        while (!_disposed)
        {
            Thread.Sleep(1000);
            try
            {
                foreach (var path in DeviceDiscovery.EnumeratePaths())
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
