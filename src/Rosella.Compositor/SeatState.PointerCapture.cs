using System.Diagnostics;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

internal sealed partial class SeatState
{
    private readonly List<(ClientSession Session, ServerObject Pointer, ServerObject Resource)> _relativePointers = [];
    private readonly List<PointerConstraint> _constraints = [];
    private PointerConstraint? _activeConstraint;

    internal void RegisterRelativePointer(ClientSession session, ServerObject pointer, ServerObject resource)
    {
        lock (_gate)
        {
            _relativePointers.Add((session, pointer, resource));
        }
    }

    internal void UnregisterRelativePointer(ServerObject resource)
    {
        lock (_gate)
        {
            _relativePointers.RemoveAll(entry => entry.Resource == resource);
        }
    }

    internal void RegisterConstraint(PointerConstraint constraint)
    {
        lock (_gate)
        {
            _constraints.Add(constraint);
            constraint.Surface.PointerConstraint = constraint;
            UpdateConstraint();
        }
        Dirty?.Invoke();
    }

    internal void UnregisterConstraint(PointerConstraint constraint)
    {
        bool active;
        lock (_gate)
        {
            active = ReferenceEquals(_activeConstraint, constraint);
            if (active)
            {
                _activeConstraint = null;
            }
            _constraints.Remove(constraint);
            if (ReferenceEquals(constraint.Surface.PointerConstraint, constraint))
            {
                constraint.Surface.PointerConstraint = null;
            }
        }
        if (active)
        {
            Dirty?.Invoke();
        }
    }

    internal void RefreshPointerConstraint()
    {
        bool changed;
        lock (_gate)
        {
            changed = UpdateConstraint();
        }
        if (changed)
        {
            Dirty?.Invoke();
        }
    }

    // Seat gate held. Require the focused window as well as the hovered
    // surface so Alt+Tab always gives the pointer back to Rosella.
    private bool UpdateConstraint()
    {
        var candidate = HasPointerDevice && _pointerFocus is { PointerConstraint: { Finished: false } next } focus &&
            focus.IsInputVisible && _keyboardFocus is { } keyboard && RootWindow(focus.MainSurface) == RootWindow(keyboard) &&
            focus.AcceptsLocalInput(_pointerX, _pointerY) && next.Contains(_pointerX, _pointerY) ? next : null;
        if (ReferenceEquals(candidate, _activeConstraint))
        {
            return false;
        }
        if (_activeConstraint is { } previous)
        {
            previous.Finished |= previous.OneShot;
            TrySend(previous.Session, previous.Resource, previous.Locked ? "unlocked" : "unconfined");
        }
        _activeConstraint = candidate;
        if (candidate is not null)
        {
            TrySend(candidate.Session, candidate.Resource, candidate.Locked ? "locked" : "confined");
        }
        return true;
    }

    internal SurfaceHandler? CapturedPointer(Scene scene)
    {
        lock (_gate)
        {
            var window = _activeConstraint?.Surface;
            return window?.Scene == scene ? window : null;
        }
    }

    internal SurfaceHandler? ConstrainPointer(Scene scene, (int X, int Y) previous, ref (int X, int Y) current, out bool locked)
    {
        lock (_gate)
        {
            locked = false;
            if (_activeConstraint is not { } capture || capture.Surface.Scene != scene)
            {
                return null;
            }
            locked = capture.Locked;
            if (locked)
            {
                current = previous;
                return capture.Surface;
            }

            // Walk along each axis to clip at the first edge, including holes
            // and disconnected regions, while still sliding along boundaries.
            var (x, y) = previous;
            var surface = capture.Surface;
            bool Allowed(int sx, int sy) => surface.AcceptsInputAt(sx, sy) &&
                capture.Contains(surface.LocalX(sx), surface.LocalY(sy));

            var stepX = Math.Sign(current.X - x);
            while (x != current.X && Allowed(x + stepX, y))
            {
                x += stepX;
            }
            var stepY = Math.Sign(current.Y - y);
            while (y != current.Y && Allowed(x, y + stepY))
            {
                y += stepY;
            }
            current = (x, y);
            return surface;
        }
    }

    internal void SendRelativeMotion(SurfaceHandler target, double dx, double dy)
    {
        lock (_gate)
        {
            SendRelativeMotionLocked(target, dx, dy);
        }
    }

    private void SendRelativeMotionLocked(SurfaceHandler target, double dx, double dy)
    {
        if (_relativePointers.Count == 0 || (dx == 0 && dy == 0) || target != _pointerFocus)
        {
            return;
        }

        WireArgument[]? arguments = null;
        foreach (var (session, _, resource) in _relativePointers)
        {
            if (target.Session == session)
            {
                arguments ??= RelativeMotionArguments(target, dx, dy);
                TrySend(session, resource, "relative_motion", arguments);
            }
        }
    }

    private static WireArgument[] RelativeMotionArguments(SurfaceHandler target, double dx, double dy)
    {
        static WireArgument Delta(double value) =>
            WireArgument.FixedPoint(Fixed.FromDouble(Math.Clamp(value, -8_388_608, 8_388_607)));
        // Match the units of absolute surface coordinates. Keep the original
        // logical deltas available to clients using unaccelerated input.
        var x = dx * target.ContentWidth / Math.Max(1, target.Width);
        var y = dy * target.ContentHeight / Math.Max(1, target.Height);
        var time = (ulong)Stopwatch.GetElapsedTime(0).Ticks / 10;
        return [WireArgument.UInt32((uint)(time >> 32)), WireArgument.UInt32((uint)time), Delta(x), Delta(y), Delta(dx), Delta(dy)];
    }
}
