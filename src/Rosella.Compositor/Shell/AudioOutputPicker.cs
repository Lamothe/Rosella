#if ROSELLA_DASHBOARD_WIDGETS
using Rosella.Compositor;

namespace Rosella.DashboardWidgets;
#elif ROSSELLA_TASKBAR
using Rosella.Compositor;

namespace Rosella.Taskbar;
#else
namespace Rosella.Compositor;
#endif

internal sealed class AudioOutputPicker(IVolumeControl volume, Action levelChanged, Action dirty)
{
    private const int Pad = 16;
    private const int HeaderHeight = 32;
    private const int RowHeight = 34;
    private const int RowsPerPage = 4;
    private const int FooterHeight = 30;
    private const uint Hover = 0xFF42424A;
    private const uint Selected = 0xFF273B54;

    private readonly object _gate = new();
    private Timer? _refreshTimer;
    private AudioOutput[]? _outputs;
    private bool _active;
    private bool _choosing;
    private bool _refreshing;
    private bool _switching;
    private bool _switchFailed;
    private int _selectionGeneration;
    private int _page;

    internal void SetActive(bool active)
    {
        lock (_gate)
        {
            _active = active;
            _choosing = false;
            _page = 0;
            _switchFailed = false;
            if (active)
            {
                _refreshTimer ??= new Timer(_ => RefreshOutputs());
                _refreshTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(2));
            }
            else
            {
                _refreshTimer?.Dispose();
                _refreshTimer = null;
            }
        }
    }

    internal int? PanelHeight
    {
        get
        {
            lock (_gate)
            {
                if (!_choosing)
                {
                    return null;
                }

                var count = _outputs?.Length ?? 0;
                var rows = Math.Clamp(count, 1, RowsPerPage);
                var footer = HasFooter(count) ? FooterHeight : 0;
                return (2 * Pad) + HeaderHeight + (rows * RowHeight) + footer;
            }
        }
    }

    private void RefreshOutputs()
    {
        int generation;
        lock (_gate)
        {
            if (!_active || _refreshing || _switching)
            {
                return;
            }

            _refreshing = true;
            generation = _selectionGeneration;
        }

        var outputs = volume.ReadOutputs();
        bool changed;
        bool defaultChanged;
        lock (_gate)
        {
            _refreshing = false;
            if (!_active || generation != _selectionGeneration)
            {
                return;
            }

            changed = _outputs is null || !_outputs.SequenceEqual(outputs);
            defaultChanged = _outputs is not null && DefaultName(_outputs) != DefaultName(outputs);
            _outputs = outputs;
            _page = Math.Min(_page, LastPage(outputs.Length));
        }

        if (defaultChanged)
        {
            levelChanged();
        }
        if (changed)
        {
            dirty();
        }
    }

    private static string? DefaultName(AudioOutput[] outputs) =>
        outputs.FirstOrDefault(output => output.IsDefault)?.Name;

    private void SwitchOutput(string name)
    {
        var changed = volume.SetDefaultOutput(name);
        var outputs = volume.ReadOutputs();
        // WirePlumber publishes the effective route asynchronously.
        for (var attempt = 0; changed && DefaultName(outputs) != name && attempt < 3; attempt++)
        {
            Thread.Sleep(50);
            outputs = volume.ReadOutputs();
        }

        levelChanged();
        lock (_gate)
        {
            _outputs = outputs;
            _switching = false;
            _switchFailed = DefaultName(outputs) != name;
            _page = Math.Min(_page, LastPage(outputs.Length));
        }
        dirty();
    }

    internal void Show()
    {
        lock (_gate)
        {
            _choosing = true;
            _switchFailed = false;
            _page = 0;
            _refreshTimer?.Change(TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
    }

    internal bool Back()
    {
        lock (_gate)
        {
            if (!_choosing)
            {
                return false;
            }

            _choosing = false;
            return true;
        }
    }

    internal bool HandlePress(Rect panel, int x, int y)
    {
        lock (_gate)
        {
            if (!_choosing)
            {
                return false;
            }

            if (HeaderRect(panel).Contains(x, y))
            {
                _choosing = false;
                return true;
            }
            if (_switching || _outputs is not { Length: > 0 } outputs)
            {
                return true;
            }

            if (outputs.Length > RowsPerPage)
            {
                var previous = PageButton(panel, next: false);
                var next = PageButton(panel, next: true);
                if (previous.Contains(x, y) || next.Contains(x, y))
                {
                    _page = Math.Clamp(_page + (next.Contains(x, y) ? 1 : -1), 0, LastPage(outputs.Length));
                    return true;
                }
            }

            var start = _page * RowsPerPage;
            for (var index = start; index < Math.Min(start + RowsPerPage, outputs.Length); index++)
            {
                var output = outputs[index];
                if (!output.IsDefault && ChoiceRect(panel, index - start).Contains(x, y))
                {
                    _switching = true;
                    _switchFailed = false;
                    _selectionGeneration++;
                    _ = Task.Run(() => SwitchOutput(output.Name));
                    break;
                }
            }

            return true;
        }
    }

    private bool HasFooter(int count) => count > RowsPerPage || _switching || _switchFailed;

    private static int LastPage(int count) => Math.Max(0, (count - 1) / RowsPerPage);

    private static Rect HeaderRect(Rect panel) => new(panel.X + Pad, panel.Y + Pad, Math.Max(0, panel.W - (2 * Pad)), HeaderHeight);

    internal static Rect ChoiceRect(Rect panel, int row) =>
        new(panel.X + Pad, panel.Y + Pad + HeaderHeight + (row * RowHeight), Math.Max(0, panel.W - (2 * Pad)), RowHeight - 3);

    private static Rect PageButton(Rect panel, bool next) =>
        new(panel.X + panel.W - Pad - (next ? 26 : 58), panel.Y + panel.H - Pad - FooterHeight + 2, 26, 26);

    internal void DrawSummary(Span<byte> pixels, int width, int height, int stride, int scale, Rect row, (int X, int Y)? pointer)
    {
        string label;
        lock (_gate)
        {
            label = _outputs?.FirstOrDefault(output => output.IsDefault)?.Label
                ?? (_outputs is null ? "Detecting devices…" : "No active output");
        }

        SettingsSelector.Draw(pixels, width, height, stride, scale, row, pointer, "Audio output", label);
    }

    internal void DrawChoices(Span<byte> pixels, int width, int height, int stride, int scale, Rect panel, (int X, int Y)? pointer)
    {
        AudioOutput[]? outputs;
        bool switching;
        bool failed;
        int page;
        lock (_gate)
        {
            outputs = _outputs;
            switching = _switching;
            failed = _switchFailed;
            page = _page;
        }

        var header = HeaderRect(panel);
        if (IsHovered(header, pointer))
        {
            Chrome.FillRoundedRect(pixels, width, height, stride, header, 8, Hover, scale);
        }
        PowerIcons.Draw(pixels, width, height, stride, PowerGlyph.ChevronLeft,
            header.X + 14, header.Y + HeaderHeight / 2, Chrome.PanelText, scale, 16);
        DrawText(pixels, width, height, stride, scale, "Audio output", header.X + 30,
            header.Y + HeaderHeight / 2, header.W - 36, Chrome.PanelText);

        if (outputs is not { Length: > 0 })
        {
            DrawText(pixels, width, height, stride, scale, outputs is null ? "Detecting devices…" : "No output devices",
                header.X + 10, header.Y + HeaderHeight + RowHeight / 2, header.W - 20, Chrome.PanelSecondaryText);
        }
        else
        {
            var start = page * RowsPerPage;
            for (var index = start; index < Math.Min(start + RowsPerPage, outputs.Length); index++)
            {
                var output = outputs[index];
                var row = ChoiceRect(panel, index - start);
                var colour = output.IsDefault ? Selected : IsHovered(row, pointer) ? Hover : Chrome.PanelControlBackground;
                Chrome.FillRoundedRect(pixels, width, height, stride, row, 8, colour, scale);
                DrawText(pixels, width, height, stride, scale, output.Label, row.X + 12, row.Y + row.H / 2,
                    row.W - 42, Chrome.PanelText);
                if (output.IsDefault)
                {
                    PowerIcons.Draw(pixels, width, height, stride, PowerGlyph.Check,
                        row.X + row.W - 16, row.Y + row.H / 2, 0xFF7BB7FF, scale, 16);
                }
            }
        }

        var showPages = outputs is { Length: > RowsPerPage };
        if (switching || failed || showPages)
        {
            var status = switching ? "Switching output…" : failed ? "Couldn’t switch output" : $"{page + 1} / {LastPage(outputs!.Length) + 1}";
            DrawText(pixels, width, height, stride, scale, status, header.X + 10,
                panel.Y + panel.H - Pad - FooterHeight / 2, header.W - (showPages ? 74 : 20), Chrome.PanelSecondaryText);
        }
        if (showPages)
        {
            DrawPageButton(pixels, width, height, stride, scale, panel, pointer, next: false, enabled: !switching && page > 0);
            DrawPageButton(pixels, width, height, stride, scale, panel, pointer, next: true, enabled: !switching && page < LastPage(outputs!.Length));
        }
    }

    private static void DrawPageButton(Span<byte> pixels, int width, int height, int stride, int scale,
        Rect panel, (int X, int Y)? pointer, bool next, bool enabled)
    {
        var button = PageButton(panel, next);
        if (enabled && IsHovered(button, pointer))
        {
            Chrome.FillRoundedRect(pixels, width, height, stride, button, 6, Hover, scale);
        }
        PowerIcons.Draw(pixels, width, height, stride, next ? PowerGlyph.ChevronRight : PowerGlyph.ChevronLeft,
            button.X + button.W / 2, button.Y + button.H / 2, enabled ? Chrome.PanelText : 0xFF777780, scale, 15);
    }

    private static bool IsHovered(Rect rect, (int X, int Y)? pointer) =>
        pointer is { } point && rect.Contains(point.X, point.Y);

    private static void DrawText(Span<byte> pixels, int width, int height, int stride, int scale,
        string text, int x, int centerY, int available, uint colour)
    {
        const int em = 13;
        var clipped = ShellText.Fit(text, em, Math.Max(0, available));
        ShellText.DrawText(pixels, width, height, stride, clipped, x * scale,
            (centerY - ShellText.LineHeight(em) / 2) * scale, colour, em, scale);
    }
}
