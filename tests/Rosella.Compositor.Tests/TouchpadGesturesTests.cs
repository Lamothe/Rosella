using Rosella.Input;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The touchpad gesture policy, pinned: tap-to-click by finger count, scroll
/// on multi-finger movement, tap-then-drag with the button held, and a real
/// button always beating a pending tap. Device units here are raw
/// multitouch coordinates — the engine does no scaling.
/// </summary>
public class TouchpadGesturesTests
{
    private const int Left = InputConstants.ButtonLeft;

    private const int Right = InputConstants.ButtonRight;

    private const int Middle = InputConstants.ButtonMiddle;

    /// <summary>Records what the engine emits, in order.</summary>
    private sealed class Sink
    {
        public List<(int Dx, int Dy)> Moves { get; } = new();

        public List<(int Dx, int Dy)> Scrolls { get; } = new();

        public List<(int Code, bool Pressed)> Buttons { get; } = new();

        public TouchpadGestures Build() =>
            new(
                (dx, dy, _) => Moves.Add((dx, dy)),
                (dx, dy, _) => Scrolls.Add((dx, dy)),
                (code, pressed, _) => Buttons.Add((code, pressed)));
    }

    private static void Down(TouchpadGestures g, int slot, uint timeMs)
    {
        g.Slot(slot);
        g.TrackingId(id: 7, timeMs);
    }

    private static void Up(TouchpadGestures g, int slot, uint timeMs)
    {
        g.Slot(slot);
        g.TrackingId(InputConstants.TrackingIdUp, timeMs);
    }

    /// <summary>Seeds and moves one finger through slot state.</summary>
    private static void Place(TouchpadGestures g, int slot, int x, int y, uint timeMs)
    {
        g.Slot(slot);
        g.PositionX(x, timeMs);
        g.PositionY(y, timeMs);
    }

    private static void Move(TouchpadGestures g, int slot, int x, int y, uint timeMs)
    {
        g.Slot(slot);
        g.PositionX(x, timeMs);
        g.PositionY(y, timeMs);
    }

    [Fact]
    public void Tap_OneFinger_SendsLeftClick()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Up(g, 0, 200);

        Assert.Equal([(Left, true), (Left, false)], sink.Buttons);
    }

    [Fact]
    public void Tap_TwoFingers_SendsRightClick()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Down(g, 1, 110);
        Place(g, 1, 600, 300, 110);
        Up(g, 0, 200);
        Up(g, 1, 200);

        Assert.Equal([(Right, true), (Right, false)], sink.Buttons);
    }

    [Fact]
    public void Tap_ThreeFingers_SendsMiddleClick()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Down(g, 1, 110);
        Place(g, 1, 600, 300, 110);
        Down(g, 2, 120);
        Place(g, 2, 700, 300, 120);
        Up(g, 0, 200);
        Up(g, 1, 200);
        Up(g, 2, 200);

        Assert.Equal([(Middle, true), (Middle, false)], sink.Buttons);
    }

    [Fact]
    public void Tap_FourFingers_SendsNothing()
    {
        var sink = new Sink();
        var g = sink.Build();
        for (var slot = 0; slot < 4; slot++)
        {
            Down(g, slot, 100);
            Place(g, slot, 500 + (slot * 100), 300, 100);
        }

        for (var slot = 0; slot < 4; slot++)
        {
            Up(g, slot, 200);
        }

        Assert.Empty(sink.Buttons);
    }

    [Fact]
    public void Tap_MovedTooFar_SendsNothing()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Move(g, 0, 500 + TouchpadGestures.TapSlop + 1, 300, 150);
        Up(g, 0, 200);

        Assert.Empty(sink.Buttons);
    }

    [Fact]
    public void Tap_TooSlow_SendsNothing()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Up(g, 0, 100 + TouchpadGestures.TapTimeoutMs + 1);

        Assert.Empty(sink.Buttons);
    }

    [Fact]
    public void Tap_AfterPhysicalButton_SendsNothing()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        g.NotePhysicalButton(Left, pressed: true, 150);
        Up(g, 0, 200);

        Assert.Empty(sink.Buttons);
    }

    [Fact]
    public void Tap_WhilePhysicalButtonHeld_SendsNothing()
    {
        var sink = new Sink();
        var g = sink.Build();

        // The button went down before the touch began and is still held.
        g.NotePhysicalButton(Left, pressed: true, 50);
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Up(g, 0, 200);

        Assert.Empty(sink.Buttons);
    }

    [Fact]
    public void OneFinger_MovesThePointer()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Move(g, 0, 520, 280, 150);

        // 20 units of X travel and −20 of Y; the engine emits raw deltas.
        Assert.Equal([(20, 0), (0, -20)], sink.Moves);
    }

    [Fact]
    public void TwoFingers_ScrollInsteadOfMovingThePointer()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Down(g, 1, 110);
        Place(g, 1, 600, 300, 110);

        // Both fingers travel +40 in Y: the pair scrolls at the mean rate
        // (40 per finger event pair over two fingers = 20 each), and the
        // pointer never moves.
        Move(g, 0, 500, 340, 150);
        Move(g, 1, 600, 340, 150);

        Assert.Empty(sink.Moves);
        Assert.Equal([(0, 20), (0, 20)], sink.Scrolls);
    }

    [Fact]
    public void Scroll_ThenFingersLift_SuppressesTheTap()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Down(g, 1, 110);
        Place(g, 1, 600, 300, 110);
        Move(g, 0, 500, 340, 150);
        Up(g, 0, 200);
        Up(g, 1, 200);

        // The pair scrolled, so lifting both is not a two-finger tap.
        Assert.Empty(sink.Buttons);
    }

    [Fact]
    public void PhysicalButton_CancelsAPendingDrag()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Up(g, 0, 150); // tap arms the drag

        Down(g, 0, 250);
        Place(g, 0, 500, 300, 250);
        g.NotePhysicalButton(Left, pressed: true, 260);
        g.NotePhysicalButton(Left, pressed: false, 270);
        Move(g, 0, 560, 300, 300);
        Up(g, 0, 400);

        // The physical click won: the movement is pointer motion, and the
        // only buttons are the tap's click (the hardware is external here).
        Assert.Equal([(60, 0)], sink.Moves);
        Assert.Equal([(Left, true), (Left, false)], sink.Buttons);
    }

    [Fact]
    public void Drag_PhysicalPressMidDragHandsOverToHardware()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Up(g, 0, 150);
        Down(g, 0, 250);
        Place(g, 0, 500, 300, 250);
        Move(g, 0, 560, 300, 300); // drag engaged: press held

        g.NotePhysicalButton(Left, pressed: true, 350);

        // Tap click, then the drag's press, then — before the hardware takes
        // over — the synthesised release: the client never sees two
        // left-down events.
        Assert.Equal(
            [(Left, true), (Left, false), (Left, true), (Left, false)],
            sink.Buttons);

        Up(g, 0, 400);

        // The drag's own release is gone with the handover.
        Assert.Equal(
            [(Left, true), (Left, false), (Left, true), (Left, false)],
            sink.Buttons);
    }

    [Fact]
    public void Elapsed_IsWrapSafe()
    {
        var sink = new Sink();
        var g = sink.Build();
        var beforeWrap = uint.MaxValue - 100u;
        Down(g, 0, beforeWrap);
        Place(g, 0, 500, 300, beforeWrap);
        Up(g, 0, 100u); // ~200ms later, across the uint wrap

        Assert.Equal([(Left, true), (Left, false)], sink.Buttons);
    }

    [Fact]
    public void Scroll_MeanRateHalvesOneMovingFinger()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Down(g, 1, 110);
        Place(g, 1, 600, 300, 110);

        // Only one finger of the pair moves 20 units: the mean is 10.
        Move(g, 0, 520, 300, 150);

        Assert.Equal([(10, 0)], sink.Scrolls);
    }

    [Fact]
    public void TapThenQuickSecondTouch_DragsWithButtonHeld()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Up(g, 0, 150); // the tap: one left click arms the drag
        Assert.Equal([(Left, true), (Left, false)], sink.Buttons);

        Down(g, 0, 250);
        Place(g, 0, 500, 300, 250);

        // The press waits for real movement: resting here sends nothing.
        Assert.Equal([(Left, true), (Left, false)], sink.Buttons);

        Move(g, 0, 500 + TouchpadGestures.TapSlop + 1, 300, 300);
        Up(g, 0, 400);

        Assert.Equal(
            [
                (Left, true),
                (Left, false),
                (Left, true),
                (Left, false),
            ],
            sink.Buttons);
    }

    [Fact]
    public void SecondTouch_RestingWithoutMoving_IsNotADrag()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Up(g, 0, 150);

        // The second touch rests past every timeout and lifts unmoved: no
        // held button may appear (a press-at-touch-down would fire one).
        Down(g, 0, 250);
        Place(g, 0, 500, 300, 250);
        Up(g, 0, 250 + TouchpadGestures.TapTimeoutMs + 1);

        Assert.Equal([(Left, true), (Left, false)], sink.Buttons);
    }

    [Fact]
    public void TapThenQuickTap_IsADoubleClick()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Up(g, 0, 150);
        Down(g, 0, 250);
        Place(g, 0, 500, 300, 250);
        Up(g, 0, 300);

        // The second touch never moved: it is a second click, not a drag.
        Assert.Equal(
            [
                (Left, true),
                (Left, false),
                (Left, true),
                (Left, false),
            ],
            sink.Buttons);
    }

    [Fact]
    public void TapThenSlowSecondTouch_IsJustAnotherTap()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Up(g, 0, 150);

        var second = 150 + TouchpadGestures.DragWindowMs + 1;
        Down(g, 0, second);
        Place(g, 0, 500, 300, second);
        Up(g, 0, second + 50);

        // Two independent taps (four clicks is wrong; two press+release).
        Assert.Equal(
            [
                (Left, true),
                (Left, false),
                (Left, true),
                (Left, false),
            ],
            sink.Buttons);
    }

    [Fact]
    public void Drag_ReleasesOnFingerUp()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Up(g, 0, 150);
        Down(g, 0, 250);
        Place(g, 0, 500, 300, 250);
        Move(g, 0, 560, 300, 300);
        Up(g, 0, 400);

        // The drag's press is not released until the finger lifts.
        Assert.Equal(
            [
                (Left, true),
                (Left, false),
                (Left, true),
                (Left, false),
            ],
            sink.Buttons);
    }

    [Fact]
    public void Reset_ReleasesAHeldDrag()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Place(g, 0, 500, 300, 100);
        Up(g, 0, 150);
        Down(g, 0, 250);
        Place(g, 0, 500, 300, 250);
        Move(g, 0, 560, 300, 300);
        g.Reset();

        Assert.Equal(
            [
                (Left, true),
                (Left, false),
                (Left, true),
                (Left, false),
            ],
            sink.Buttons);
    }

    [Fact]
    public void StrayCoordinates_AreIgnored()
    {
        var sink = new Sink();
        var g = sink.Build();
        g.Slot(0);
        g.PositionX(500, 100);
        g.PositionY(300, 100);
        Up(g, 0, 200);

        // No tracking id ever landed: coordinates and the up are strays.
        Assert.Empty(sink.Moves);
        Assert.Empty(sink.Buttons);
    }

    [Fact]
    public void Tap_ArmsOnlyLeftClicksForDrag()
    {
        var sink = new Sink();
        var g = sink.Build();
        Down(g, 0, 100);
        Down(g, 1, 100);
        Place(g, 0, 500, 300, 100);
        Place(g, 1, 600, 300, 100);
        Up(g, 0, 150);
        Up(g, 1, 150); // two-finger tap = right click; no drag may arm

        Down(g, 0, 250);
        Place(g, 0, 500, 300, 250);
        Move(g, 0, 560, 300, 300);

        // The move is plain pointer motion, not a held-button drag.
        Assert.Equal([(60, 0)], sink.Moves);
        Assert.Equal(
            [(Right, true), (Right, false)],
            sink.Buttons);
    }
}
