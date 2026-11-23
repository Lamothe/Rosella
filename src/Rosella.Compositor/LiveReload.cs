using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(Rosella.Compositor.LiveReload))]

namespace Rosella.Compositor;

internal static class LiveReload
{
    internal static Action? Repaint { get; set; }

    internal static void ReportProcess()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_WATCH") == "1" &&
            Environment.GetEnvironmentVariable("ROSELLA_LIVE_PROCESS_FILE") is { } path)
        {
            File.WriteAllText(path, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable("ROSELLA_LIVE_PROCESS_FILE", null);
        }
    }

    public static void ClearCache(Type[]? updatedTypes) => PowerIcons.ClearCache();

    public static void UpdateApplication(Type[]? updatedTypes) => Repaint?.Invoke();
}
