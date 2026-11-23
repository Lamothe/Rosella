namespace Rosella.Input;

/// <summary>
/// One open input device node, as the compositor's reader stack sees it.
/// The interface exists so the hot-plug and grab machinery can be tested
/// without touching real <c>/dev/input</c> nodes.
/// </summary>
public interface IInputDevice : IDisposable
{
    string Name { get; }

    void Grab();

    void Ungrab();

    InputEvent ReadEvent();
}

/// <summary>Where input devices come from; the tests supply fakes.</summary>
public interface IDeviceSource
{
    IReadOnlyList<string> EnumeratePaths();

    IInputDevice? TryOpen(string path);

    bool IsKeyboard(IInputDevice device);

    bool IsPointer(IInputDevice device);
}

/// <summary>The real evdev source over <c>/dev/input</c>.</summary>
public sealed class EvdevDeviceSource : IDeviceSource
{
    public IReadOnlyList<string> EnumeratePaths() => DeviceDiscovery.EnumeratePaths();

    // TryOpen only ever yields EvdevDevice, so the classification casts back.
    public IInputDevice? TryOpen(string path) => EvdevDevice.TryOpen(path);

    public bool IsKeyboard(IInputDevice device) => DeviceDiscovery.IsKeyboard((EvdevDevice)device);

    public bool IsPointer(IInputDevice device) => DeviceDiscovery.IsPointer((EvdevDevice)device);
}
