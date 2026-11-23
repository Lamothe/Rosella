using Rosella.Drm;

namespace Rosella.Compositor;

internal sealed class ResolutionPicker(DrmOutput output)
{
    private const int RowHeight = 34;
    private const int PageSize = 7;
    private readonly (ushort Width, ushort Height)[] _sizes = output.Connector.Modes
        .Where(mode => !mode.IsInterlaced)
        .OrderByDescending(mode => mode.Area)
        .Select(mode => (mode.HDisplay, mode.VDisplay)).Distinct().ToArray();
    private int _page;
    internal bool IsOpen { get; set; }
    private string _summary = $"{output.Mode.HDisplay} × {output.Mode.VDisplay}";
    internal string Summary => _summary;
    private int Count => Math.Min(PageSize, _sizes.Length - _page * PageSize);
    internal int Height => (Count + 2) * RowHeight + 12;

    internal static DrmOutput Select(DrmOutput current)
    {
        try
        {
            var size = File.ReadAllText(PathFor(current)).Trim();
            return current.Connector.Modes
                .Where(mode => !mode.IsInterlaced && size == $"{mode.HDisplay}x{mode.VDisplay}")
                .OrderBy(mode => Math.Abs(mode.RefreshHz - current.Mode.RefreshHz))
                .Select(mode => current with { Mode = mode }).FirstOrDefault() ?? current;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return current;
        }
    }

    private static string PathFor(DrmOutput output)
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (config is null || !Path.IsPathFullyQualified(config))
        {
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(config, "rosella", $"resolution-{Environment.MachineName}-{Scanout.GnomeConnectorName(output.Connector)}");
    }

    internal static void Discard(DrmOutput output)
    {
        try
        {
            File.Delete(PathFor(output));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"rosella: could not clear the rejected resolution: {error.Message}");
        }
    }

    internal void Press(Rect panel, int y)
    {
        var row = (y - panel.Y - 6) / RowHeight;
        if (row == 0) { IsOpen = false; return; }
        if (row == Count + 1)
        {
            _page = (_page + 1) % Math.Max(1, (_sizes.Length + PageSize - 1) / PageSize);
            return;
        }
        if (row < 1 || row > Count) { return; }
        var (width, height) = _sizes[_page * PageSize + row - 1];
        var path = PathFor(output);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".new";
            File.WriteAllText(temporary, $"{width}x{height}\n");
            File.Move(temporary, path, overwrite: true);
            _summary = $"{width} × {height} · Next login";
            IsOpen = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _summary = "Could not save resolution";
            IsOpen = false;
            Console.Error.WriteLine($"rosella: {error.Message}");
        }
    }

    internal void Draw(Span<byte> pixels, int width, int height, int stride, int scale, Rect panel)
    {
        DrawRow(pixels, width, height, stride, scale, new Rect(panel.X + 16, panel.Y + 6, panel.W - 32, RowHeight), "‹  Resolution at next login");
        for (var i = 0; i < Count; i++)
        {
            var (w, h) = _sizes[_page * PageSize + i];
            DrawRow(pixels, width, height, stride, scale, new Rect(panel.X + 16, panel.Y + 6 + (i + 1) * RowHeight, panel.W - 32, RowHeight), $"{w} × {h}");
        }
        if (_sizes.Length > PageSize)
        {
            DrawRow(pixels, width, height, stride, scale, new Rect(panel.X + 16, panel.Y + 6 + (Count + 1) * RowHeight, panel.W - 32, RowHeight), "More resolutions ›");
        }
    }

    private static void DrawRow(Span<byte> pixels, int width, int height, int stride, int scale, Rect row, string label) =>
        ShellText.DrawText(pixels, width, height, stride, label, (row.X + 8) * scale, (row.Y + 9) * scale, Chrome.PanelText, 13, scale);
}
