namespace Rosella.Input;

/// <summary>Finds the evdev device nodes on the system.</summary>
public static class DeviceDiscovery
{
    /// <summary>
    /// The <c>/dev/input/event*</c> nodes in numeric order. Returns an empty
    /// list when /dev/input does not exist (headless CI, tests).
    /// </summary>
    public static IReadOnlyList<string> EnumeratePaths()
    {
        if (!Directory.Exists("/dev/input"))
        {
            return [];
        }

        return Directory.EnumerateFiles("/dev/input", "event*")
            .OrderBy(path => int.TryParse(Path.GetFileName(path).AsSpan("event".Length), out var number) ? number : int.MaxValue)
            .ToArray();
    }

    /// <summary>True for devices that carry keyboard keys (not power buttons).</summary>
    public static bool IsKeyboard(EvdevDevice device) =>
        device.HasEventType(InputConstants.EventKey)
        && (device.HasKey(InputConstants.KeyEnter) || device.HasKey(InputConstants.KeyA));

    /// <summary>True for devices that move a cursor: a pointer with X/Y motion.</summary>
    public static bool IsPointer(EvdevDevice device) =>
        device.HasEventType(InputConstants.EventRelative)
        && device.HasRelative(InputConstants.RelativeX)
        && device.HasRelative(InputConstants.RelativeY);
}
