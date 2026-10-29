using Rosella.Control;
using Rosella.Input;

namespace Rosella.Compositor;

/// <summary>
/// The control socket's hands and eyes on a live session. Everything here
/// is the real thing: frames come from the render loop's buffers, and
/// injected input becomes ordinary evdev-shaped events in the same router
/// a trackpad feeds. What the driver sees is what a person at the machine
/// would see and do.
/// </summary>
internal sealed class ControlAdapter : IControlTarget
{
    private readonly Scene _scene;
    private readonly InputRouter _router;
    private readonly Scanout _scanout;
    private readonly ScreenPower _screenPower;

    internal ControlAdapter(Scene scene, InputRouter router, Scanout scanout, ScreenPower screenPower)
    {
        _scene = scene;
        _router = router;
        _scanout = scanout;
        _screenPower = screenPower;
    }

    public byte[] CaptureFrame()
    {
        // Never call this from the render loop (it is the loop's
        // counterpart). A wedged loop must not wedge the driver forever.
        return _scanout.CaptureFrame().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    public string SetBlanked(bool blank)
    {
        // The policy flips here and the render loop performs the DPMS change
        // on its next turn, so wake it first — otherwise a blank could sit
        // unapplied for the length of a long idle wait.
        var action = _screenPower.Force(blank);
        _scene.MarkDirty();
        return action switch
        {
            ScreenPowerAction.Blank => "blanked",
            ScreenPowerAction.Unblank => "awake",
            _ => "unchanged",
        };
    }

    public string SurfaceTable() => string.Join('\n', _scene.DescribeSurfaces());

    public string State()
    {
        var cursor = _router.Cursor;
        var lines = new List<string>
        {
            $"output {_scene.OutputSize.Width}x{_scene.OutputSize.Height} logical",
            $"screen {(_screenPower.IsBlanked ? "blanked" : "awake")}",
            $"cursor {cursor.X},{cursor.Y}",
            "surfaces:",
        };
        lines.AddRange(_scene.DescribeSurfaces().Select(line => "  " + line));
        return string.Join('\n', lines) + "\n";
    }

    public void RelativeMotion(int dx, int dy)
    {
        var (sec, usec) = Now();
        _router.ProcessEvent(new InputEvent(sec, usec, InputConstants.EventRelative, InputConstants.RelativeX, dx));
        _router.ProcessEvent(new InputEvent(sec, usec, InputConstants.EventRelative, InputConstants.RelativeY, dy));
    }

    public void WarpCursor(int x, int y) => _router.PlaceCursor(x, y);

    public void Button(ushort code, int value) => InjectKey(code, value);

    public void Key(ushort code, int value) => InjectKey(code, value);

    private void InjectKey(ushort code, int value)
    {
        var (sec, usec) = Now();
        _router.ProcessEvent(new InputEvent(sec, usec, InputConstants.EventKey, code, value));
    }

    private static (long Sec, long Usec) Now()
    {
        var now = DateTimeOffset.UtcNow;
        return (now.ToUnixTimeSeconds(), (long)now.TimeOfDay.TotalMicroseconds % 1_000_000);
    }
}
