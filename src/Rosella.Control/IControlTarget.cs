namespace Rosella.Control;

/// <summary>
/// What the control socket can drive. The compositor implements this over
/// its real scene and input router, so everything a control client does is
/// the same code path a hand on a trackpad takes. Tests implement it over a
/// fake.
/// </summary>
public interface IControlTarget
{
    /// <summary>The composited frame as PPM bytes, captured from the render thread.</summary>
    byte[] CaptureFrame();

    /// <summary>The surface state table (the same text the frame dump logs).</summary>
    string SurfaceTable();

    /// <summary>A one-screen summary: outputs, windows, cursor.</summary>
    string State();

    /// <summary>Relative pointer motion, in the same units an evdev device reports.</summary>
    void RelativeMotion(int dx, int dy);

    /// <summary>Warp the cursor to logical coordinates (a test affordance, not an evdev event).</summary>
    void WarpCursor(int x, int y);

    /// <summary>Pointer button: value 1 = down, 0 = up.</summary>
    void Button(ushort code, int value);

    /// <summary>Key: value 1 = down, 0 = up (evdev key codes).</summary>
    void Key(ushort code, int value);

    /// <summary>
    /// Forces the screen dark or lit regardless of idleness (a test hand for
    /// blanking). Returns the action that actually happened.
    /// </summary>
    string SetBlanked(bool blank);
}
