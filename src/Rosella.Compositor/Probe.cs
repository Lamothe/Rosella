using Rosella.Drm;

namespace Rosella.Compositor;

/// <summary>
/// Prints what the DRM cards, connectors and drivers offer, and whether dumb
/// buffers work. Read-only introspection apart from a create/map/destroy
/// round trip: safe to run inside a live session.
/// </summary>
internal static class Probe
{
    internal static void Run()
    {
        foreach (var path in DrmDevice.CardPaths())
        {
            Console.WriteLine($"== {path}");
            try
            {
                using var device = DrmDevice.Open(path);
                Print(device);
            }
            catch (Exception exception) when (exception is DrmException or UnauthorizedAccessException or IOException)
            {
                Console.WriteLine($"   cannot use: {exception.Message}");
            }
        }
    }

    private static void Print(DrmDevice device)
    {
        var version = device.GetVersion();
        Console.WriteLine($"   driver: {version.Name} {version.Major}.{version.Minor}.{version.Patch} ({version.Date})");
        Console.WriteLine($"   description: {version.Description}");
        Console.WriteLine($"   CAP_DUMB_BUFFER: {device.GetCap(DrmConstants.CapDumbBuffer)}");

        var resources = device.GetResources();
        Console.WriteLine($"   crtcs: {string.Join(", ", resources.CrtcIds)}");
        Console.WriteLine($"   encoders: {string.Join(", ", resources.EncoderIds)}");

        foreach (var connectorId in resources.ConnectorIds)
        {
            var connector = device.GetConnector(connectorId);
            Console.WriteLine($"   connector {connector.Id}: {connector} {connector.MmWidth}x{connector.MmHeight}mm");
            foreach (var encoderId in connector.EncoderIds)
            {
                var encoder = device.GetEncoder(encoderId);
                Console.WriteLine($"      encoder {encoder.Id}: crtc={encoder.CrtcId} possible_crtcs=0x{encoder.PossibleCrtcs:X}");
            }

            for (var i = 0; i < connector.Modes.Count && i < 8; i++)
            {
                var mode = connector.Modes[i];
                Console.WriteLine($"      mode: {mode.Name} {mode.HDisplay}x{mode.VDisplay}@{mode.VRefresh}{(mode.IsPreferred ? " (preferred)" : string.Empty)}");
            }

            if (connector.Modes.Count > 8)
            {
                Console.WriteLine($"      … and {connector.Modes.Count - 8} more modes");
            }
        }

        try
        {
            using var buffer = device.CreateDumb(64, 64, 32);
            buffer.Pixels[0] = 0xAA;
            Console.WriteLine($"   dumb buffers: OK (handle {buffer.Handle}, pitch {buffer.Pitch}, {buffer.Size} bytes)");
        }
        catch (DrmException exception)
        {
            Console.WriteLine($"   dumb buffers: FAILED — {exception.Message}");
        }
    }
}
