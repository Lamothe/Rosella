using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rosella.Compositor;

/// <summary>The PipeWire endpoints the settings panel exposes a volume for.</summary>
internal enum AudioEndpoint
{
    Output,
    Input,
}

internal enum VolumeShortcut
{
    Raise,
    Lower,
    ToggleMute,
}

internal readonly record struct AudioLevel(int Percent, bool IsMuted);
internal sealed record AudioOutput(uint Id, string Name, string Label, bool IsDefault);

/// <summary>Volume access for quick settings and the media keys.</summary>
internal interface IVolumeControl
{
    AudioLevel? ReadLevel(AudioEndpoint endpoint);

    bool SetPercent(AudioEndpoint endpoint, int percent);

    bool ApplyShortcut(AudioEndpoint endpoint, VolumeShortcut shortcut);

    AudioOutput[] ReadOutputs() => [];

    bool SetDefaultOutput(string name) => false;
}

/// <summary>Reads and writes the PipeWire default sink and source through wpctl.</summary>
internal sealed class WpctlVolumeControl : IVolumeControl
{
    private static readonly Regex VolumePattern = new(@"\bVolume:\s*(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public AudioLevel? ReadLevel(AudioEndpoint endpoint)
    {
        var result = Run("wpctl", "get-volume", DefaultNode(endpoint));
        if (result.ExitCode != 0)
        {
            return null;
        }

        var match = VolumePattern.Match(result.Output);
        return match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var volume)
            ? new AudioLevel(Math.Clamp((int)Math.Round(volume * 100), 0, 150),
                result.Output.Contains("[MUTED]", StringComparison.Ordinal))
            : null;
    }

    public bool SetPercent(AudioEndpoint endpoint, int percent)
    {
        var normalized = Math.Clamp(percent, 0, 150) / 100d;
        return Run("wpctl", "set-volume", DefaultNode(endpoint), normalized.ToString("0.00", CultureInfo.InvariantCulture)).ExitCode == 0;
    }

    public bool ApplyShortcut(AudioEndpoint endpoint, VolumeShortcut shortcut)
    {
        var node = DefaultNode(endpoint);
        return shortcut switch
        {
            VolumeShortcut.Raise => Run("wpctl", "set-volume", "--limit", "1.0", node, "5%+").ExitCode == 0,
            VolumeShortcut.Lower => Run("wpctl", "set-volume", node, "5%-").ExitCode == 0,
            VolumeShortcut.ToggleMute => Run("wpctl", "set-mute", node, "toggle").ExitCode == 0,
            _ => false,
        };
    }

    internal static string DefaultNode(AudioEndpoint endpoint) =>
        endpoint == AudioEndpoint.Output ? "@DEFAULT_AUDIO_SINK@" : "@DEFAULT_AUDIO_SOURCE@";

    public AudioOutput[] ReadOutputs()
    {
        var result = Run("pw-dump");
        return result.ExitCode == 0 ? ParseOutputs(result.Output) : [];
    }

    public bool SetDefaultOutput(string name)
    {
        // PipeWire can recycle numeric IDs when a device disappears. Resolve
        // the chosen node name again before asking WirePlumber to switch.
        var output = ReadOutputs().FirstOrDefault(output => output.Name == name);
        return output is not null && (output.IsDefault ||
            Run("wpctl", "set-default", output.Id.ToString(CultureInfo.InvariantCulture)).ExitCode == 0);
    }

    internal static AudioOutput[] ParseOutputs(string json)
    {
        try
        {
            using var dump = JsonDocument.Parse(json);
            if (dump.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var devices = new List<(uint Id, string Name, string Label, string Description)>();
            string? defaultName = null;
            foreach (var entry in dump.RootElement.EnumerateArray())
            {
                switch (StringProperty(entry, "type"))
                {
                    case "PipeWire:Interface:Metadata" when StringProperty(Property(entry, "props"), "metadata.name") == "default":
                        var metadata = Property(entry, "metadata");
                        if (metadata.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in metadata.EnumerateArray())
                            {
                                if (StringProperty(item, "key") == "default.audio.sink")
                                {
                                    defaultName = StringProperty(Property(item, "value"), "name");
                                }
                            }
                        }
                        break;

                    case "PipeWire:Interface:Node":
                        var properties = Property(Property(entry, "info"), "props");
                        var id = Property(entry, "id");
                        if (StringProperty(properties, "media.class") == "Audio/Sink" &&
                            StringProperty(properties, "node.name") is { Length: > 0 } name &&
                            id.ValueKind == JsonValueKind.Number && id.TryGetUInt32(out var nodeId))
                        {
                            var description = StringProperty(properties, "node.description") ?? name;
                            var label = StringProperty(properties, "node.nick")?.Trim();
                            devices.Add((nodeId, name, string.IsNullOrEmpty(label) ? description : label, description));
                        }
                        break;
                }
            }

            // Keep similarly named outputs distinguishable and avoid moving
            // rows around every time a new default is chosen.
            return devices.GroupBy(output => output.Label)
                .SelectMany(group => group.Select(output => new AudioOutput(output.Id, output.Name,
                    group.Count() == 1 ? output.Label : output.Description, output.Name == defaultName)))
                .OrderBy(output => output.Label, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(output => output.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonElement Property(JsonElement entry, string name) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var value) ? value : default;

    private static string? StringProperty(JsonElement entry, string name)
    {
        var value = Property(entry, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static (int ExitCode, string Output) Run(string program, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return (-1, string.Empty);
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(1500))
            {
                process.Kill(entireProcessTree: true);
                return (-1, string.Empty);
            }

            errors.GetAwaiter().GetResult();
            return (process.ExitCode, output.GetAwaiter().GetResult());
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return (-1, string.Empty);
        }
    }
}
