using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Rosella.Compositor;

/// <summary>Volume access used by the clock-anchored settings panel.</summary>
internal interface IVolumeControl
{
    int? ReadPercent();

    bool SetPercent(int percent);
}

/// <summary>Reads and writes the PipeWire default sink through wpctl.</summary>
internal sealed class WpctlVolumeControl : IVolumeControl
{
    private static readonly Regex VolumePattern = new(@"\bVolume:\s*(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public int? ReadPercent()
    {
        var result = Run("get-volume", "@DEFAULT_AUDIO_SINK@");
        if (result.ExitCode != 0)
        {
            return null;
        }

        var match = VolumePattern.Match(result.Output);
        return match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var volume)
            ? Math.Clamp((int)Math.Round(volume * 100), 0, 150)
            : null;
    }

    public bool SetPercent(int percent)
    {
        var normalized = Math.Clamp(percent, 0, 150) / 100d;
        return Run("set-volume", "@DEFAULT_AUDIO_SINK@", normalized.ToString("0.00", CultureInfo.InvariantCulture)).ExitCode == 0;
    }

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("wpctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return (-1, string.Empty);
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                return (-1, string.Empty);
            }

            return (process.ExitCode, output);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return (-1, string.Empty);
        }
    }
}
