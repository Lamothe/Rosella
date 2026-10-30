using Rosella.Input;

namespace Rosella.Compositor.Tests;

/// <summary>
/// Device lifecycle around a VT switch: hot-plug while running grabs the
/// new device immediately; hot-plug while parked on another VT joins
/// ungrabbed and is grabbed on resume; and a failed grab or ungrab is
/// re-tried by the sweep until it sticks. A parked session must never
/// hold input hostage from the console it handed the screen to.
/// </summary>
public sealed class InputThreadTests
{
    private sealed class FakeDevice(string name) : IInputDevice
    {
        public string Name { get; } = name;

        public int Grabs { get; private set; }

        public int Ungrabs { get; private set; }

        public bool FailNextGrab { get; set; }

        public bool FailNextUngrab { get; set; }

        public void Grab()
        {
            if (FailNextGrab)
            {
                FailNextGrab = false;
                throw new IOException("simulated grab failure");
            }

            Grabs++;
        }

        public void Ungrab()
        {
            if (FailNextUngrab)
            {
                FailNextUngrab = false;
                throw new IOException("simulated ungrab failure");
            }

            Ungrabs++;
        }

        public InputEvent ReadEvent() => throw new NotSupportedException("tests never start readers");

        public void Dispose()
        {
        }
    }

    private sealed class FakeSource : IDeviceSource
    {
        private readonly Dictionary<string, FakeDevice> _devices = new();
        private readonly HashSet<string> _keyboards = new();
        private readonly HashSet<string> _pointers = new();

        public FakeDevice Add(string path, bool keyboard, bool pointer)
        {
            var device = new FakeDevice(path);
            _devices[path] = device;
            if (keyboard)
            {
                _keyboards.Add(path);
            }

            if (pointer)
            {
                _pointers.Add(path);
            }

            return device;
        }

        public IReadOnlyList<string> EnumeratePaths() => [.. _devices.Keys];

        public IInputDevice? TryOpen(string path) => _devices.GetValueOrDefault(path);

        public bool IsKeyboard(IInputDevice device) => _keyboards.Contains(device.Name);

        public bool IsPointer(IInputDevice device) => _pointers.Contains(device.Name);
    }

    [Fact]
    public void HotPlug_WhileRunning_GrabsImmediately()
    {
        var source = new FakeSource();
        var keyboard = source.Add("event0", keyboard: true, pointer: false);
        using var input = new InputThread(new Scene(), new SeatState(), () => { }, source: source);
        input.Discover();
        Assert.Equal(1, keyboard.Grabs);

        var mouse = source.Add("event1", keyboard: false, pointer: true);
        input.PollHotPlug();
        Assert.Equal(1, mouse.Grabs);
    }

    [Fact]
    public void HotPlug_WhileSuspended_DefersTheGrabUntilResume()
    {
        var source = new FakeSource();
        var keyboard = source.Add("event0", keyboard: true, pointer: false);
        using var input = new InputThread(new Scene(), new SeatState(), () => { }, source: source);
        input.Discover();
        input.Suspend();
        Assert.Equal(1, keyboard.Ungrabs);

        // A mouse plugged in while parked must not be grabbed: the console
        // owns input until we are back.
        var mouse = source.Add("event1", keyboard: false, pointer: true);
        input.PollHotPlug();
        Assert.Equal(0, mouse.Grabs);

        input.Resume();
        Assert.Equal(1, mouse.Grabs);
        Assert.Equal(2, keyboard.Grabs);
    }

    [Fact]
    public void FailedGrab_IsRetriedByTheSweep()
    {
        var source = new FakeSource();
        var mouse = source.Add("event0", keyboard: false, pointer: true);
        mouse.FailNextGrab = true;
        using var input = new InputThread(new Scene(), new SeatState(), () => { }, source: source);
        input.Discover();
        Assert.Equal(0, mouse.Grabs); // first attempt failed

        input.PollHotPlug(); // the sweep reconciles the grab
        Assert.Equal(1, mouse.Grabs);
    }

    [Fact]
    public void FailedUngrab_IsRetriedByTheSweep()
    {
        var source = new FakeSource();
        var keyboard = source.Add("event0", keyboard: true, pointer: false);
        using var input = new InputThread(new Scene(), new SeatState(), () => { }, source: source);
        input.Discover();
        keyboard.FailNextUngrab = true;
        input.Suspend();
        Assert.Equal(0, keyboard.Ungrabs); // the switch-time ungrab failed

        input.PollHotPlug(); // the sweep must not leave the console starved
        Assert.Equal(1, keyboard.Ungrabs);
    }
}
