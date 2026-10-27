using System.Net.Sockets;
using Rosella.Input;

namespace Rosella.Compositor;

/// <summary>
/// Discovers evdev devices synchronously (before clients bind, so seat
/// capabilities are right from the start), grabs them exclusively, and runs
/// one reader thread per device feeding the router. No hot-plug: the device
/// set is whatever exists at startup (M6).
/// </summary>
internal sealed class InputThread : IDisposable
{
    private readonly SeatState _seat;
    private readonly InputRouter _router;
    private readonly List<EvdevDevice> _devices = new();
    private readonly List<Thread> _threads = new();

    internal InputThread(Scene scene, SeatState seat, Action quit)
    {
        _seat = seat;
        _router = new InputRouter(scene, seat, quit);
    }

    internal InputRouter Router => _router;

    /// <summary>Opens, classifies, and grabs devices. Safe to call from tests.</summary>
    internal void Discover()
    {
        foreach (var path in DeviceDiscovery.EnumeratePaths())
        {
            if (EvdevDevice.TryOpen(path) is not { } device)
            {
                continue;
            }

            if (!DeviceDiscovery.IsKeyboard(device) && !DeviceDiscovery.IsPointer(device))
            {
                device.Dispose();
                continue;
            }

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
                continue;
            }

            _devices.Add(device);
            if (DeviceDiscovery.IsKeyboard(device))
            {
                _seat.HasKeyboardDevice = true;
            }

            if (DeviceDiscovery.IsPointer(device))
            {
                _seat.HasPointerDevice = true;
            }
        }
    }

    /// <summary>Starts one reader thread per discovered device.</summary>
    internal void Start()
    {
        foreach (var device in _devices)
        {
            var captured = device;
            var thread = new Thread(() => ReadLoop(captured))
            {
                IsBackground = true,
                Name = $"rosella-input-{captured.Name}",
            };
            thread.Start();
            _threads.Add(thread);
        }
    }

    public void Dispose()
    {
        foreach (var device in _devices)
        {
            device.Dispose();
        }

        foreach (var thread in _threads)
        {
            thread.Join(1000);
        }
    }

    private void ReadLoop(EvdevDevice device)
    {
        try
        {
            while (true)
            {
                var e = device.ReadEvent();
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
    }
}
