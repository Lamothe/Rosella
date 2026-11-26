using Rosella.Server;

using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>
/// A full-frame overlay drawn instead of the desktop scene: the greeter's
/// login screen. It has no clients underneath and paints every pixel itself
/// (background included), so the scene never composites the desktop or the
/// pointer while one is set.
/// </summary>
internal interface ISceneOverlay
{
    /// <summary>
    /// Paints the whole output. The buffer is physical pixels; the overlay
    /// scales its own logical layout by <paramref name="scale"/>.
    /// </summary>
    void Draw(Span<byte> pixels, int width, int height, int stride, int scale);
}

/// <summary>
/// The compositor's scene: windows in mapping order over the desktop
/// background, shared between client threads and the scanout loop.
/// </summary>
public sealed class Scene
{
    private static int _nextLockOrder;
    private readonly int _lockOrder = Interlocked.Increment(ref _nextLockOrder);
    private readonly object _gate = new();
    private readonly List<SurfaceHandler> _surfaces = new();
    private readonly List<XdgPopupHandler> _popupGrabs = new();
    private bool _dirty = true;
    // Synchronized clients can commit indefinitely without a parent commit.
    // Bound retained copies and state-only closures separately for each client.
    internal const long MaxCachedCommitBytes = 128L * 1024 * 1024;
    internal const int MaxCachedCommitCount = 1024;
    // Bound all recursive tree, geometry and cached-commit traversals, including
    // trees assembled bottom-up or reattached with existing descendants.
    internal const int MaxSubsurfaceDepth = 64;
    private sealed class CacheBudget
    {
        internal long Bytes;
        internal int Count;
    }
    private readonly object _cacheGate = new();
    private readonly Dictionary<object, CacheBudget> _cacheBudgets = new();

    /// <summary>The pixels this frame has to repaint (guarded by <see cref="_gate"/>).</summary>
    private readonly DamageTracker _damage = new();

    /// <summary>Scratch for the parts of a damage region no opaque window covers; render-loop only.</summary>
    private readonly RectList _uncovered = new();
    private readonly Dictionary<SurfaceHandler, int> _idleInhibitors = new();
    private readonly RectList _inhibitorVisible = new();

    internal object Gate => _gate;
    internal Scene? OtherDesktop { get; set; }

    internal bool TryMoveWindowHere(SurfaceHandler selected)
    {
        if (OtherDesktop is not { } source || selected.Scene != source || !selected.IsMapped || selected.Gone)
        {
            return false;
        }

        var window = selected.XdgRole?.Toplevel?.RootSurface ?? selected;
        foreach (var task in source.TaskWindows())
        {
            if (BelongsTo(task.Surface, window))
            {
                source.DismissDescendantPopups(task.Surface);
            }
        }
        SurfaceHandler[] moved = [];
        WithBoth(source, this, () =>
        {
            if (selected.Scene != source || !selected.IsMapped || selected.Gone || selected.XdgRole?.Toplevel is null)
            {
                return;
            }

            window = selected.XdgRole.Toplevel.RootSurface;
            moved = source._surfaces.Where(surface => BelongsTo(surface, window)).ToArray();
            foreach (var surface in moved)
            {
                source.TransferSurface(surface, this);
                if (surface.XdgRole is { Toplevel: not null } role && surface.IsMapped)
                {
                    role.Reflow();
                }
            }
            selected.Hidden = false;
            Raise(selected);
            source.MarkDirty();
            MarkDirty();
        });

        foreach (var surface in moved)
        {
            surface.OutputChanged();
        }
        return moved.Length > 0;
    }

    private static bool BelongsTo(SurfaceHandler surface, SurfaceHandler window)
    {
        var main = surface.MainSurface;
        var toplevel = main.XdgRole?.RootToplevelSurface ?? main;
        return ReferenceEquals(toplevel.XdgRole?.Toplevel?.RootSurface ?? toplevel, window);
    }

    internal static void AttachToParent(SurfaceHandler child, SurfaceHandler parent, Action<Scene> attach)
    {
        while (true)
        {
            var from = child.Scene;
            var to = parent.Scene;
            SurfaceHandler[] transferred = [];
            var attached = false;
            WithBoth(from, to, () =>
            {
                if (child.Scene != from || parent.Scene != to)
                {
                    return;
                }

                var children = from == to ? [] : from._surfaces.Where(surface =>
                    surface.MainSurface == child || surface.MainSurface.XdgRole?.RootToplevelSurface == child).ToArray();
                // Validate the protocol request before changing ownership.
                attach(to);
                if (from != to)
                {
                    foreach (var surface in children)
                    {
                        from.TransferSurface(surface, to);
                        if (surface.XdgRole is { Toplevel: not null } role && surface.IsMapped)
                        {
                            role.Reflow();
                        }
                    }
                    transferred = children;
                    from.MarkDirty();
                    to.MarkDirty();
                }
                attached = true;
            });

            if (attached)
            {
                foreach (var surface in transferred)
                {
                    surface.OutputChanged();
                }
                return;
            }
        }
    }

    private void TransferSurface(SurfaceHandler surface, Scene destination)
    {
        _surfaces.Remove(surface);
        destination._surfaces.Add(surface);
        if (_idleInhibitors.Remove(surface, out var count))
        {
            destination._idleInhibitors[surface] = count;
        }
        _popupGrabs.RemoveAll(popup => popup.Surface == surface);
        surface.Scene = destination;
    }

    internal static void WithBoth(Scene a, Scene b, Action action)
    {
        var (first, second) = a._lockOrder <= b._lockOrder ? (a, b) : (b, a);
        lock (first._gate)
        lock (second._gate)
        {
            action();
        }
    }

    internal void AddIdleInhibitor(SurfaceHandler surface)
    {
        lock (_gate)
        {
            _idleInhibitors[surface] = _idleInhibitors.GetValueOrDefault(surface) + 1;
            MarkDirty(DamageRect.Empty);
        }
    }

    internal void RemoveIdleInhibitor(SurfaceHandler surface)
    {
        lock (_gate)
        {
            if (_idleInhibitors.TryGetValue(surface, out var count))
            {
                if (count == 1)
                {
                    _idleInhibitors.Remove(surface);
                }
                else
                {
                    _idleInhibitors[surface] = count - 1;
                }
                MarkDirty(DamageRect.Empty);
            }
        }
    }

    internal bool IdleInhibited()
    {
        lock (_gate)
        {
            if (_idleInhibitors.Count == 0)
            {
                return false;
            }

            var output = DamageRect.For(OutputSize.Width, OutputSize.Height);
            var ordered = OrderedSurfaces().ToArray();
            for (var index = ordered.Length - 1; index >= 0; index--)
            {
                var surface = ordered[index];
                if (surface.IsCursorSurface || !_idleInhibitors.ContainsKey(surface))
                {
                    continue;
                }

                _inhibitorVisible.Reset(WindowRect(surface, 1).Intersect(output));
                for (var above = index + 1; above < ordered.Length && _inhibitorVisible.Count > 0; above++)
                {
                    if (IsOpaqueWindow(ordered[above]))
                    {
                        _inhibitorVisible.Subtract(WindowRect(ordered[above], 1));
                    }
                }

                if (_inhibitorVisible.Overflowed || _inhibitorVisible.Count > 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Something on screen changed: the scanout loop composites on the next
    /// wake instead of sleeping. Everything visible funnels through here —
    /// commits, cursor moves, focus, shell state — so an idle desktop does
    /// no rendering work at all.
    /// </summary>
    internal void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
            _damage.MarkFull();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Something changed only inside <paramref name="rect"/>: the loop wakes
    /// and repaints that region instead of the whole output. Callers that
    /// cannot say what changed keep using the no-argument overload, which
    /// means "repaint everything" — over-painting is a cost, under-painting
    /// is a bug.
    /// </summary>
    internal void MarkDirty(in DamageRect rect)
    {
        lock (_gate)
        {
            _dirty = true;
            _damage.Add(rect);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Takes this frame's damage, clipped to an output of the given size.
    /// Zero rects means nothing changed (the loop can skip the frame
    /// entirely); one whole-output rect means everything did.
    /// </summary>
    internal int TakeDamage(int width, int height, Span<DamageRect> destination)
    {
        ExpireMagnify();
        ExpireSpinner();
        lock (_gate)
        {
            if (Shell?.Screenshot.TakeDamage(width, height, destination) is { } screenshotDamage)
            {
                return screenshotDamage;
            }

            var count = _damage.Take(width, height, destination);
            if (count == 0 || (count == 1 && destination[0] == DamageRect.For(width, height)) ||
                Shell is not { DockVisible: true, LastBarLayout: { } layout } shell)
            {
                return count;
            }

            // A client update just outside the glass can change its blurred edge.
            // Include that edge before the scanout records damage for both buffers.
            var scale = Math.Max(1, Scale);
            var dock = layout.Bounds.ToDamage(scale);
            var panel = shell.Settings.IsOpen
                ? shell.Settings.PanelRect(layout.Clock, width / scale, height / scale).ToDamage(scale)
                : DamageRect.Empty;
            for (var i = 0; i < count; i++)
            {
                var spread = destination[i].Inflate(FrostedGlass.BlurRadius * scale);
                _damage.Add(destination[i].Union(spread.Intersect(dock)).Union(spread.Intersect(panel)));
            }

            return _damage.Take(width, height, destination);
        }
    }

    /// <summary>
    /// Advances the launch spinner before the frame takes its damage: a
    /// rotation step or a resolved launch is a picture change, and the
    /// spinner dirties whatever it changed.
    /// </summary>
    private void ExpireSpinner() => Shell?.Spinner.Advance();

    /// <summary>
    /// Waits until the scene is dirty or <paramref name="maxWait"/> passes
    /// (bound for clock ticks). Returns false when the token is cancelled.
    /// Consumes the dirty flag; a change arriving during the subsequent
    /// render re-arms it and the next frame picks it up.
    /// </summary>
    internal bool WaitForDirty(TimeSpan maxWait, CancellationToken token)
    {
        // Cancellation must end the wait promptly: the token itself pulses
        // the gate, so the loop below re-checks it on every wake.
        using var wake = token.Register(() =>
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        });

        lock (_gate)
        {
            while (!_dirty)
            {
                if (token.IsCancellationRequested)
                {
                    return false;
                }

                if (!Monitor.Wait(_gate, maxWait))
                {
                    break;
                }
            }

            _dirty = false;
            return !token.IsCancellationRequested;
        }
    }

    /// <summary>Registers a surface; it gets its position in the cascade.</summary>
    internal void Register(SurfaceHandler surface)
    {
        lock (_gate)
        {
            surface.Position = Cascade(_surfaces.Count);
            _surfaces.Add(surface);

            // Joining, leaving and restacking change what is on screen in
            // ways the scene cannot name: damage the whole output. A wake
            // with no damage would otherwise paint nothing and leave the
            // departed window's pixels up (see MarkDirty).
            _damage.MarkFull();
            _dirty = true;
            Monitor.PulseAll(_gate);
        }
    }

    internal void Remove(SurfaceHandler surface)
    {
        surface.WithScene(scene =>
        {
            // Gone first: a stale hit-test reference must not be re-focused.
            surface.Gone = true;
            scene.DetachTree(surface);
            scene._surfaces.Remove(surface);
            scene._idleInhibitors.Remove(surface);
            scene._popupGrabs.RemoveAll(popup => ReferenceEquals(popup.Surface, surface));
            scene.MarkDirty();
        });

        // Destroy paths only: the surface id is dying or gone, so focus is
        // cleared without leave events (they would name a dead object).
        surface.Scene.Seat?.OnSurfaceHidden(surface, canSendLeave: false);
    }

    internal void AddSubsurface(ClientSession session, Rosella.Server.ServerObject target, uint id, SurfaceHandler child, SurfaceHandler parent)
    {
        lock (_gate)
        {
            if (child.HasAnyRole && child.Subsurface is not { IsDefunct: true })
            {
                throw new Rosella.Server.ProtocolErrorException(target.Id, 0, "surface already has a role");
            }

            var parentDepth = 0;
            for (SurfaceHandler? ancestor = parent; ancestor is not null; ancestor = ancestor.Subsurface?.Parent)
            {
                if (ancestor == child)
                {
                    throw new Rosella.Server.ProtocolErrorException(target.Id, 1, "invalid subsurface parent");
                }
                parentDepth++;
            }

            // PendingChildren includes descendants not yet published by a commit.
            // Count them too: a role can be attached after its subtree exists.
            var pending = new Stack<(SurfaceHandler Surface, int Depth)>();
            pending.Push((child, parentDepth));
            while (pending.TryPop(out var item))
            {
                if (item.Depth > MaxSubsurfaceDepth)
                {
                    throw new Rosella.Server.ProtocolErrorException(1, ClientSession.ErrorNoMemory, "subsurface depth limit exceeded");
                }
                foreach (var descendant in item.Surface.PendingChildren)
                {
                    if (descendant != item.Surface)
                    {
                        pending.Push((descendant, item.Depth + 1));
                    }
                }
            }

            var role = new SubsurfaceHandler(child, parent);
            HandlerSupport.CreateChild(session, target, "wl_subsurface", role, id);
            child.AssignSubsurface(role);
            EnsureStack(parent);
            parent.PendingChildren.Add(child);
        }
    }

    private static void EnsureStack(SurfaceHandler surface)
    {
        if (surface.PendingChildren.Count == 0)
        {
            surface.PendingChildren.Add(surface);
            surface.Children.Add(surface);
        }
    }

    internal void SetSubsurfacePosition(SubsurfaceHandler role, (int X, int Y) position)
    {
        lock (_gate) { role.PendingPosition = position; }
    }

    internal void RestackSubsurface(SubsurfaceHandler role, SurfaceHandler reference, bool above, Rosella.Server.ServerObject target)
    {
        lock (_gate)
        {
            var parent = role.Parent;
            if (parent is null || reference == role.Surface || !parent.PendingChildren.Contains(reference))
            {
                throw new Rosella.Server.ProtocolErrorException(target.Id, 0, "not a parent or sibling");
            }

            parent.PendingChildren.Remove(role.Surface);
            var index = parent.PendingChildren.IndexOf(reference);
            parent.PendingChildren.Insert(index + (above ? 1 : 0), role.Surface);
        }
    }

    internal void SetSubsurfaceSync(SubsurfaceHandler role, bool sync)
    {
        lock (_gate)
        {
            role.Sync = sync;
            FlushDesynchronised(role.Surface);
        }
    }

    private void FlushDesynchronised(SurfaceHandler surface)
    {
        if (surface.Subsurface is { EffectiveSync: false })
        {
            foreach (var commit in TakeCached(surface)) { commit.Apply(); }
        }

        foreach (var child in surface.PendingChildren.ToArray())
        {
            if (child != surface) { FlushDesynchronised(child); }
        }
    }

    /// <summary>Called after a toplevel's first buffer is visible, outside the scene lock.</summary>
    internal Action<SurfaceHandler>? ToplevelMapped { get; set; }

    internal void ApplySurfaceCommit(SurfaceHandler surface, CachedSurfaceCommit content)
    {
        lock (_gate)
        {
            var stack = surface.PendingChildren.ToArray();
            var positions = stack.Where(child => child != surface && child.Subsurface?.Parent == surface)
                .Select(child => (Role: child.Subsurface!, Position: child.Subsurface!.PendingPosition)).ToArray();
            var childCommits = stack.Where(child => child != surface && child.Subsurface?.EffectiveSync == true)
                .SelectMany(TakeCached).ToArray();
            var role = surface.Subsurface;
            void Cancel()
            {
                content.Cancel();
                foreach (var child in childCommits) { child.Cancel(); }
            }
            void Apply()
            {
                if (surface.Gone || (role is not null && (role.IsDefunct || surface.Subsurface != role)))
                {
                    Cancel();
                    return;
                }
                var treeChanged = !surface.Children.SequenceEqual(stack.Where(child => child == surface || child.Subsurface?.Parent == surface))
                    || positions.Any(item => item.Role.Parent == surface && item.Role.Position != item.Position);
                content.Apply();
                surface.Children.Clear();
                surface.Children.AddRange(stack.Where(child => child == surface || child.Subsurface?.Parent == surface));
                foreach (var (childRole, position) in positions)
                {
                    if (childRole.Parent == surface) { childRole.Position = position; }
                }
                foreach (var child in childCommits) { child.Apply(); }
                if (treeChanged) { surface.Scene.MarkDirty(); }
            }

            if (surface.Subsurface is { EffectiveSync: true })
            {
                // Cached closures can outlive a detached tree and be nested again
                // after reattachment, so bound their depth independently.
                var commitDepth = 1 + childCommits.Select(commit => commit.Depth).DefaultIfEmpty(0).Max();
                if (commitDepth > MaxSubsurfaceDepth)
                {
                    Cancel();
                    throw new Rosella.Server.ProtocolErrorException(1, ClientSession.ErrorNoMemory, "synchronized commit depth limit exceeded");
                }
                // Unbound surfaces are used by unit tests; group them by their tree.
                var owner = (object?)surface.Session ?? surface.MainSurface;
                var home = surface.HomeScene;
                CacheBudget? budget;
                lock (home._cacheGate)
                {
                    if (!home._cacheBudgets.TryGetValue(owner, out budget))
                    {
                        budget = new CacheBudget();
                        home._cacheBudgets.Add(owner, budget);
                    }
                    if (budget.Count >= MaxCachedCommitCount || content.CopiedBytes > MaxCachedCommitBytes - budget.Bytes)
                    {
                        budget = null;
                    }
                    else
                    {
                        budget.Count++;
                        budget.Bytes += content.CopiedBytes;
                    }
                }
                if (budget is null)
                {
                    Cancel();
                    throw new Rosella.Server.ProtocolErrorException(1, ClientSession.ErrorNoMemory, "synchronized commit cache limit exceeded");
                }
                void Release()
                {
                    lock (home._cacheGate)
                    {
                        budget.Count--;
                        budget.Bytes -= content.CopiedBytes;
                        if (budget.Count == 0) { home._cacheBudgets.Remove(owner); }
                    }
                }
                surface.CachedCommits.Add(new CachedSurfaceCommit(
                    () => { Release(); Apply(); }, () => { Release(); Cancel(); }, Depth: commitDepth));
            }
            else { Apply(); }
        }
    }

    private static CachedSurfaceCommit[] TakeCached(SurfaceHandler surface)
    {
        var actions = surface.CachedCommits.ToArray();
        surface.CachedCommits.Clear();
        return actions;
    }

    internal void RemoveSubsurface(SubsurfaceHandler role)
    {
        lock (_gate)
        {
            // Clear descendant focus while the ancestry is still intact.
            role.Surface.UnmapContent();
            if (role.Parent is { } parent)
            {
                parent.Children.Remove(role.Surface);
                parent.PendingChildren.Remove(role.Surface);
            }
            role.Parent = null;
            role.Surface.DiscardCachedCommits();
            FlushDesynchronised(role.Surface);
            MarkDirty();
        }
    }

    internal void DetachTree(SurfaceHandler surface)
    {
        lock (_gate)
        {
            foreach (var child in surface.PendingChildren.ToArray())
            {
                if (child != surface && child.Subsurface is { } role) { RemoveSubsurface(role); }
            }
        }
    }

    private IEnumerable<SurfaceHandler> OrderedSurfaces()
    {
        // Clients can reuse a popup surface after its parent has been raised.
        // Keep popups above their parent and below unrelated higher windows.
        var popups = _surfaces
            .Where(surface => surface.IsMapped)
            .Select(surface => (Surface: surface, Parent: surface.XdgRole?.PopupParent?.Surface))
            .Where(popup => popup.Parent is not null)
            .ToLookup(popup => popup.Parent!, popup => popup.Surface);
        foreach (var surface in _surfaces)
        {
            if (surface.Subsurface is null && surface.XdgRole?.PopupParent is null)
            {
                foreach (var item in OrderedWindow(surface, popups)) { yield return item; }
            }
        }
    }

    private static IEnumerable<SurfaceHandler> OrderedWindow(
        SurfaceHandler surface, ILookup<SurfaceHandler, SurfaceHandler> popups)
    {
        if (surface.Gone || !surface.IsMapped || surface.Hidden) { yield break; }
        foreach (var item in OrderedTree(surface)) { yield return item; }
        foreach (var popup in popups[surface])
        {
            foreach (var item in OrderedWindow(popup, popups)) { yield return item; }
        }
    }

    private static IEnumerable<SurfaceHandler> OrderedTree(SurfaceHandler surface)
    {
        if (surface.Gone || !surface.IsMapped || surface.Hidden) { yield break; }
        if (surface.Children.Count == 0) { yield return surface; yield break; }
        foreach (var child in surface.Children)
        {
            if (child == surface) { yield return surface; }
            else { foreach (var item in OrderedTree(child)) { yield return item; } }
        }
    }

    /// <summary>The scanout's logical size (physical pixels / Scale); windows are placed in it.</summary>
    internal (int Width, int Height) OutputSize { get; set; } = (1920, 1080);

    /// <summary>
    /// Physical pixels per logical pixel on this output (ScalePolicy): all
    /// scene coordinates are logical, rendering multiplies by this.
    /// </summary>
    internal int Scale { get; set; } = 1;

    /// <summary>Where windows live: the output minus the taskbar band.</summary>
    internal Rect WorkArea => new(0, 0, OutputSize.Width, OutputSize.Height);

    /// <summary>
    /// The greeter's login screen, when this scene belongs to a greeter. A
    /// full-screen overlay owns every pixel, so a partial damage region still
    /// produces a complete frame (see <see cref="RenderRegion"/>).
    /// </summary>
    internal ISceneOverlay? Overlay { get; set; }

    /// <summary>The desktop shell, when one is wired in (taskbar and settings panel).</summary>
    internal Shell? Shell
    {
        get => _shell;
        set
        {
            _shell = value;
            if (value is not null)
            {
                value.Settings.Dirty = MarkDirty;
                value.Notifications.Dirty = MarkDirty;
                value.Switcher.Dirty = MarkDirty;
                // The spinner needs the scene's clock, its list of live app
                // ids (a window appearing ends the spin), and the dirty
                // callback so a rotation step re-composites.
                value.Spinner.Now = Now;
                value.Spinner.LiveAppIds = () => TaskWindows().Select(task => task.AppId).ToArray();
                value.Spinner.Dirty = MarkCursorDirty;
            }
        }
    }

    private Shell? _shell;

    /// <summary>
    /// The input seat, notified when surfaces hide or go away. Setting it
    /// wires the seat's dirty callback (cursor image changes re-composite).
    /// </summary>
    internal SeatState? Seat
    {
        get => _seat;
        set
        {
            if (_seat is not null)
            {
                _seat.Dirty -= MarkActiveCursorDirty;
            }
            _seat = value;
            if (value is not null)
            {
                value.Dirty += MarkActiveCursorDirty;
            }
        }
    }

    private SeatState? _seat;

    private void MarkActiveCursorDirty()
    {
        if (_desktopActive)
        {
            MarkDirty();
        }
    }

    internal SurfaceHandler? FocusedWindow => Seat?.KeyboardFocus is { } focus && focus.Scene == this ? focus : null;

    /// <summary>Where the cursor arrow's hotspot sits (guarded by the scene gate).</summary>
    internal (int X, int Y) Cursor
    {
        get
        {
            lock (_gate)
            {
                return _cursor;
            }
        }

        set
        {
            // Read the pointer footprint before taking the gate: the seat and
            // surface locks are never taken underneath it.
            var footprint = CursorFootprint(Magnified);
            lock (_gate)
            {
                var previous = _cursor;
                if (previous == value)
                {
                    return;
                }

                _cursor = value;
                _dirty = true;

                var shell = Shell;
                var scale = Math.Max(1, Scale);
                // The screenshot has its own damage while the live scene is hidden.
                if (shell?.Screenshot.PointerMoved(previous, value, scale) != true)
                {
                    if (shell?.UpdateDockReveal(value.Y, OutputSize.Height) == true)
                    {
                        _damage.MarkFull();
                    }
                    else if (_desktopActive && shell is not null)
                    {
                        if (shell is { DockVisible: true, LastBarLayout: { } layout })
                        {
                            DamageHoverChange(layout.HoverBounds(previous), layout.HoverBounds(value), scale);
                        }
                        if (shell.DockMenu is { } menu)
                        {
                            DamageHoverChange(menu.HoverBounds(previous), menu.HoverBounds(value), scale);
                        }
                    }

                    _damage.Add(CursorDamage(previous, footprint));
                    _damage.Add(CursorDamage(value, footprint));
                }

                Monitor.PulseAll(_gate);
            }
        }
    }

    private (int X, int Y) _cursor;
    private volatile bool _desktopActive = true;

    // A hover transition changes the whole highlight, outside the cursor footprint.
    private void DamageHoverChange(Rect previous, Rect current, int scale)
    {
        if (previous != current)
        {
            _damage.Add(previous.ToDamage(scale));
            _damage.Add(current.ToDamage(scale));
        }
    }

    internal void SetDesktopActive(bool active)
    {
        lock (_gate)
        {
            _desktopActive = active;
            Shell?.UpdateDockReveal(active ? _cursor.Y : -1, OutputSize.Height);
            MarkDirty();
        }
    }

    /// <summary>How much larger a shaken pointer is drawn.</summary>
    internal const int MagnifyFactor = 3;

    /// <summary>How long the enlarged pointer stays before shrinking back.</summary>
    internal static readonly TimeSpan MagnifyHold = TimeSpan.FromMilliseconds(1000);

    /// <summary>Milliseconds since boot, injectable so tests can age the pointer.</summary>
    internal Func<long> Now { get; set; } = () => Environment.TickCount64;

    /// <summary>When the enlargement ends (guarded by <see cref="_gate"/>); 0 when not magnified.</summary>
    private long _magnifyUntil;

    /// <summary>Whether the pointer is currently drawn enlarged (guarded by <see cref="_gate"/>).</summary>
    private bool Magnified
    {
        get
        {
            lock (_gate)
            {
                return _magnifyUntil != 0;
            }
        }
    }

    /// <summary>
    /// Enlarges the pointer for a moment — the shake-to-find gesture. The
    /// pointer grows around its hotspot and shrinks back after
    /// <see cref="MagnifyHold"/>; shaking again during the hold restarts the
    /// clock. The growth is a different image from the theme (crisp, not a
    /// resample) at <see cref="MagnifyFactor"/> the nominal size.
    /// </summary>
    internal void MagnifyCursor()
    {
        // Both footprints are read before the gate: the seat and surface
        // locks are never taken underneath it.
        var normal = CursorFootprint(magnified: false);
        var large = CursorFootprint(magnified: true);
        lock (_gate)
        {
            _magnifyUntil = Now() + (long)MagnifyHold.TotalMilliseconds;
            _dirty = true;
            _damage.Add(CursorDamage(_cursor, normal));
            _damage.Add(CursorDamage(_cursor, large));
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// How long until an enlarged pointer shrinks back, or null when it is
    /// not enlarged. The scanout loop sleeps no longer than this, so the
    /// shrink happens on time even on an idle desktop.
    /// </summary>
    internal TimeSpan? UntilPointerShrinks
    {
        get
        {
            lock (_gate)
            {
                if (_magnifyUntil == 0)
                {
                    return null;
                }

                var remaining = _magnifyUntil - Now();
                return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(remaining);
            }
        }
    }

    /// <summary>
    /// Ends an enlargement whose hold has passed. Whole-output damage: the
    /// large image has to be painted over wherever it was, and one full
    /// repaint a second after a shake is cheaper than tracking the old box
    /// through concurrent pointer moves (over-painting is a cost, under-
    /// painting is a bug).
    /// </summary>
    private void ExpireMagnify()
    {
        lock (_gate)
        {
            if (_magnifyUntil == 0 || Now() < _magnifyUntil)
            {
                return;
            }

            _magnifyUntil = 0;
            _dirty = true;
            _damage.MarkFull();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Rosella's own pointer art, loaded from the Xcursor theme the client
    /// toolkits use. Null keeps the built-in arrow (no theme found, or the
    /// compositor started before any icons were installed).
    /// </summary>
    internal CursorTheme? Cursors { get; set; }

    /// <summary>
    /// The union of every pointer image's box relative to the pointer, in
    /// physical pixels: (X, Y) is the top-left offset, Width/Height the
    /// extent. The themed pointer and a client cursor surface have
    /// independent hotspots — a client's may be negative — so they cannot
    /// share one width/hotspot pair without under-covering one of them.
    /// </summary>
    private (int X, int Y, int Width, int Height) CursorFootprint(bool magnified)
    {
        var scale = Math.Max(1, Scale);
        var factor = magnified ? MagnifyFactor : 1;
        var own = OwnPointerFootprint(scale, factor);
        var ownBox = (-own.HotspotX, -own.HotspotY, own.Width, own.Height);
        var cursor = Seat?.CurrentCursor() ?? default;
        var clientBox = cursor.Surface is { } surface
            ? ClientCursorBox(surface.ContentWidth, surface.ContentHeight, cursor.HotspotX, cursor.HotspotY, scale, factor)
            : ((int X, int Y, int Width, int Height)?)null;
        var footprint = Union(ownBox, clientBox);
        if (Seat?.Drag.Icon is { IsMapped: true, Gone: false } icon && icon.Scene == this)
        {
            footprint = Union(footprint, (0, 0, icon.ContentWidth * scale, icon.ContentHeight * scale));
        }
        return Shell is null ? footprint : Union(footprint, BusyCursorBox(scale));
    }

    private static (int X, int Y, int Width, int Height) BusyCursorBox(int scale)
    {
        var radius = LaunchSpinner.CursorRadius * scale;
        return (-radius, -radius, (2 * radius) + 1, (2 * radius) + 1);
    }

    /// <summary>Invalidates the pointer footprint after an image update at the same size.</summary>
    internal void MarkCursorDirty() => MarkCursorDirty(null);

    internal void MarkCursorDirty(SurfaceHandler? committedCursor)
    {
        if (committedCursor is not null && !ReferenceEquals(Seat?.CurrentCursor().Surface, committedCursor))
        {
            return;
        }

        var footprint = CursorFootprint(Magnified);
        lock (_gate)
        {
            MarkDirty(CursorDamage(_cursor, footprint));
        }
    }

    /// <summary>
    /// A client cursor's physical box relative to the pointer. The hotspot
    /// may be negative (the protocol allows it), and the enlargement scales
    /// the image about that hotspot, so the box keeps its own origin.
    /// </summary>
    internal static (int X, int Y, int Width, int Height) ClientCursorBox(
        int contentWidth,
        int contentHeight,
        int hotspotX,
        int hotspotY,
        int scale,
        int factor) =>
        (
            -hotspotX * scale * factor,
            -hotspotY * scale * factor,
            contentWidth * scale * factor,
            contentHeight * scale * factor
        );

    /// <summary>
    /// The union of two pointer-image boxes relative to the pointer. Each
    /// keeps its own origin: clamping or re-deriving one from the union
    /// would leave part of that image outside the damage rect.
    /// </summary>
    internal static (int X, int Y, int Width, int Height) Union(
        (int X, int Y, int Width, int Height) first,
        (int X, int Y, int Width, int Height)? second)
    {
        if (second is not { } other)
        {
            return first;
        }

        var left = Math.Min(first.X, other.X);
        var top = Math.Min(first.Y, other.Y);
        var right = Math.Max(first.X + first.Width, other.X + other.Width);
        var bottom = Math.Max(first.Y + first.Height, other.Y + other.Height);
        return (left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>
    /// The compositor's own pointer box in physical pixels: the themed bitmap
    /// is native physical pixels (as Chrome's set_cursor bitmaps are), and
    /// the arrow sprite is logical, so it scales up. Keeping this physical is
    /// what lets the damage rect use the drawn origin exactly — dividing a
    /// hotspot down and back multiplies rounding error into missing columns.
    /// </summary>
    private (int Width, int Height, int HotspotX, int HotspotY) OwnPointerFootprint(int scale, int factor)
    {
        if (Cursors?.Pointer(scale, factor) is { } bitmap)
        {
            return (bitmap.Width, bitmap.Height, bitmap.HotspotX, bitmap.HotspotY);
        }

        return (CursorSprite.Width * scale * factor, CursorSprite.Height * scale * factor, 0, 0);
    }

    /// <summary>The physical box any pointer image can touch at a position (one pixel of slack).</summary>
    private DamageRect CursorDamage((int X, int Y) position, (int X, int Y, int Width, int Height) footprint)
    {
        var scale = Math.Max(1, Scale);
        return new DamageRect(
            (position.X * scale) + footprint.X,
            (position.Y * scale) + footprint.Y,
            footprint.Width + 1,
            footprint.Height + 1);
    }

    /// <summary>Focus teardown for the seat (unmap = leave, destroy = silent).</summary>
    internal void OnSurfaceHidden(SurfaceHandler surface, bool canSendLeave)
    {
        Seat?.OnSurfaceHidden(surface, canSendLeave);
    }

    /// <summary>
    /// Places an ordinary toplevel at the work-area origin and raises it.
    /// </summary>
    internal (int X, int Y) PlaceWindow(SurfaceHandler surface, int windowWidth, int windowHeight)
    {
        lock (_gate)
        {
            _surfaces.Remove(surface);
            _surfaces.Add(surface);
            _damage.MarkFull();
            _dirty = true;
            Monitor.PulseAll(_gate);
            return (WorkArea.X, WorkArea.Y);
        }
    }

    /// <summary>One line per surface (diagnostics: the runtime truth behind a render report).</summary>
    internal string[] DescribeSurfaces()
    {
        SurfaceHandler[] snapshot;
        lock (_gate)
        {
            snapshot = _surfaces.ToArray();
        }

        return snapshot.Select(s => s.Describe()).ToArray();
    }

    /// <summary>
    /// Draws the background, the windows, the shell chrome, and the cursor —
    /// all in logical pixels scaled to the physical scanout by Scale.
    /// </summary>
    public void Render(Span<byte> pixels, int width, int height, int stride, long frame)
    {
        RenderRegion(pixels, width, height, stride, frame, DamageRect.For(width, height));
    }

    /// <summary>
    /// How long until the spinner's next rotation step, or null when
    /// nothing is loading. Like the blank policy and the pointer shrink,
    /// it bounds the scanout loop's idle sleep so the dots turn on time.
    /// </summary>
    internal TimeSpan? UntilSpinnerTick => Shell?.Spinner.UntilNextTick();

    /// <summary>
    /// Repaints one damage region of a frame from scene content. Pixels
    /// inside <paramref name="damage"/> come out identical to what a full
    /// <see cref="Render"/> would put there, while the untouched pixels cost
    /// nothing. Returns what was written — the number damage tracking exists
    /// to shrink.
    /// </summary>
    internal RenderCost RenderRegion(Span<byte> pixels, int width, int height, int stride, long frame, in DamageRect damage, bool includePointer = true)
    {
        var scale = Math.Max(1, Scale);
        var region = damage.Intersect(DamageRect.For(width, height));
        if (region.IsEmpty)
        {
            return default;
        }

        // The greeter's overlay paints the entire frame — background, card
        // and fields — so a partial damage region still yields a complete
        // picture and nothing below it (desktop, windows, cursor) belongs on
        // screen. The cost is a full repaint per keystroke, which is the
        // right trade for a login screen and keeps it independent of the
        // damage tracker's seeding.
        if (Overlay is { } overlay)
        {
            overlay.Draw(pixels, width, height, stride, scale);
            return new RenderCost((long)width * height, 0, 0);
        }

        if (includePointer && Shell?.Screenshot.Draw(pixels, stride, scale, region, Cursor) == true)
        {
            return new RenderCost(region.Area, 0, 0);
        }

        var cost = RenderWindows(pixels, width, height, stride, scale, region);

        if (Shell is { } shell && NeedsChrome(region, scale, width, height))
        {
            var taskbar = shell.Taskbar;
            var clock = taskbar.Module.ClockString();
            var tasks = TaskWindows();
            var layout = taskbar.Module.ComputeLayout(width / scale, height / scale, clock, shell.OrderTasks(tasks), shell.Pinned);
            var panel = taskbar.Settings.IsOpen ? taskbar.Settings.PanelRect(layout.Clock, width / scale, height / scale) : (Rect?)null;
            var dockVisible = shell.DockVisible || panel.HasValue;

            // Sample the freshly composited desktop, before either panel or
            // the pointer has been drawn. Clip blur work to the damaged area.
            if (dockVisible)
            {
                var dockCost = PrepareGlass(shell.DockGlass, pixels, width, height, stride, scale, layout.Bounds, region);
                cost = new RenderCost(cost.BackgroundPixels + dockCost.BackgroundPixels,
                    cost.SurfacePixels + dockCost.SurfacePixels, Math.Max(cost.Surfaces, dockCost.Surfaces));
            }
            if (panel is { } settings)
            {
                var panelCost = PrepareGlass(shell.SettingsGlass, pixels, width, height, stride, scale, settings, region);
                cost = new RenderCost(cost.BackgroundPixels + panelCost.BackgroundPixels,
                    cost.SurfacePixels + panelCost.SurfacePixels, Math.Max(cost.Surfaces, panelCost.Surfaces));
            }

            shell.Draw(taskbar, pixels, width / scale, height / scale, stride, scale, clock, tasks, layout,
                dockVisible, panel, FocusedWindow, _desktopActive ? Cursor : null);
        }
        Shell?.Notifications.Draw(pixels, width, height, stride, scale, region);

        // The cursor: the client's cursor surface when the focused client set
        // one (context shapes — text beam, hand — as the apps intend), hidden
        // when the focused client hid it, else Rosella's own pointer from the
        // cursor theme (or the built-in arrow when no theme loaded). Once a
        // client has named a cursor surface the image is that surface's —
        // never our pointer over the app's intent (content may still be in
        // flight).
        if (!includePointer || !_desktopActive)
        {
            return cost;
        }

        var (cursorX, cursorY) = Cursor;
        if (Seat?.Drag.Icon is { IsMapped: true, Gone: false } dragIcon && dragIcon.Scene == this)
        {
            dragIcon.BlitAt(pixels, width, height, stride, cursorX * scale, cursorY * scale,
                dragIcon.ContentWidth * scale, dragIcon.ContentHeight * scale, region);
        }
        var cursor = Seat?.CurrentCursor() ?? default;
        var factor = Magnified ? MagnifyFactor : 1;

        // A client's cursor is a fixed small bitmap: warping it to three
        // times its size makes its antialiased rim three pixels of mush.
        // While the pointer is enlarged the theme's own large art is drawn
        // instead — for the shake the point is to be found, not to keep the
        // app's shape for one second. A client bitmap with more pixels than
        // the theme's own art is still stretched: it is the better source,
        // and replacing it with less detailed art would lose the pointer
        // rather than find it.
        var client = cursor.Surface is { } clientSurface
            ? (Width: clientSurface.ContentWidth * scale, Height: clientSurface.ContentHeight * scale)
            : default;
        var enlarged = factor > 1 ? Cursors?.Pointer(scale, factor) : null;
        if (Shell?.Spinner is { IsActive: true } spinner)
        {
            var viewport = pixels.Slice((region.Y * stride) + (region.X * 4));
            spinner.DrawCursor(viewport, region.W, region.H, stride,
                (cursorX * scale) - region.X, (cursorY * scale) - region.Y, scale);
        }
        else if (cursor.Hidden)
        {
            // The focused client hid the pointer image (spec: NULL surface).
        }
        else if (enlarged is { } big && client.Width <= big.SourceWidth && client.Height <= big.SourceHeight)
        {
            DrawTheme(pixels, width, height, stride, cursorX, cursorY, scale, big, region);
        }
        else if (cursor.Surface is { } cursorSurface)
        {
            if (cursorSurface is { IsMapped: true, Gone: false })
            {
                // Enlarged, the client's bitmap is stretched about its
                // hotspot: nearest-neighbour, but the shape the app chose is
                // preserved. The box is the same one the damage footprint
                // uses, so the two cannot drift apart.
                var box = ClientCursorBox(
                    cursorSurface.ContentWidth,
                    cursorSurface.ContentHeight,
                    cursor.HotspotX,
                    cursor.HotspotY,
                    scale,
                    factor);
                cursorSurface.BlitAt(
                    pixels, width, height, stride,
                    (cursorX * scale) + box.X,
                    (cursorY * scale) + box.Y,
                    box.Width,
                    box.Height,
                    region);
            }
        }
        else if (Cursors?.Pointer(scale) is { } bitmap)
        {
            DrawTheme(pixels, width, height, stride, cursorX, cursorY, scale, bitmap, region);
        }
        else
        {
            // The arrow draws at the pointer with the buffer's dimensions;
            // handing it a viewport at the region's origin clips it to the
            // damage without it having to know about damage at all.
            var viewport = pixels.Slice((region.Y * stride) + (region.X * 4));
            CursorSprite.Draw(viewport, region.W, region.H, stride, (cursorX * scale) - region.X, (cursorY * scale) - region.Y, scale * factor);
        }

        return cost;
    }

    private byte[] _glassBackdrop = [];

    private RenderCost PrepareGlass(FrostedGlass glass, ReadOnlySpan<byte> pixels,
        int width, int height, int stride, int scale, Rect bounds, in DamageRect region)
    {
        var radius = FrostedGlass.BlurRadius * scale;
        var area = bounds.ToDamage(scale).Intersect(region);
        var sample = area.Inflate(radius).Intersect(DamageRect.For(width, height));
        if (sample.IsEmpty || region.Contains(sample))
        {
            glass.Prepare(pixels, width, height, stride, radius, area);
            return default;
        }

        // Neighbours outside the damage may still contain the previous frame's
        // shell or cursor. Paint the required slice of the raw scene separately.
        var length = checked(stride * height);
        if (_glassBackdrop.Length < length)
        {
            _glassBackdrop = GC.AllocateUninitializedArray<byte>(length);
        }

        var cost = RenderWindows(_glassBackdrop, width, height, stride, scale, sample);
        glass.Prepare(_glassBackdrop, width, height, stride, radius, area);
        return cost;
    }

    // Render-loop scratch, reused for each damage region.
    private readonly List<SurfaceHandler> _paintOrder = [];

    private RenderCost RenderWindows(Span<byte> pixels, int width, int height, int stride, int scale, in DamageRect region)
    {
        lock (_gate)
        {
            _paintOrder.Clear();
            foreach (var surface in OrderedSurfaces())
            {
                if (!surface.IsCursorSurface && WindowRect(surface, scale).Intersects(region))
                {
                    _paintOrder.Add(surface);
                }
            }

            // The highest opaque surface covering the whole region supplies
            // every pixel below it. Locate and paint the cover under its state
            // lock so a concurrent transparency change cannot invalidate it.
            for (var i = _paintOrder.Count - 1; i >= 0; i--)
            {
                if (!_paintOrder[i].TryBlitOpaqueCover(pixels, width, height, stride, scale, region))
                {
                    continue;
                }

                var painted = region.Area;
                for (var above = i + 1; above < _paintOrder.Count; above++)
                {
                    var surface = _paintOrder[above];
                    surface.BlitInto(pixels, width, height, stride, scale, region);
                    painted += WindowRect(surface, scale).Intersect(region).Area;
                }

                return new RenderCost(0, painted, _paintOrder.Count - i);
            }
        }

        // Opaque windows hide the desktop: only the rest of the region needs
        // the background painted under it. Repainting a whole-output
        // wallpaper behind a maximised window is most of the memcpy the
        // damage path exists to remove.
        _uncovered.Reset(region);

        // The desktop is skipped under opaque windows, so remember exactly
        // which rectangle each one excluded: a window that turns translucent
        // before the blit pass has to be compensated over that rectangle,
        // not over whatever geometry it has by then (it may have moved).
        // Reused between frames: this is the render hot path.
        _opaqueRects.Clear();
        lock (_gate)
        {
            foreach (var surface in OrderedSurfaces())
            {
                if (IsOpaqueWindow(surface))
                {
                    var rect = WindowRect(surface, scale);
                    _opaqueRects.Add((surface, rect));
                    _uncovered.Subtract(rect);
                    if (_uncovered.Overflowed)
                    {
                        break;
                    }
                }
            }
        }

        long backgroundPixels = 0;
        if (_uncovered.Overflowed)
        {
            backgroundPixels += Background.RenderRegion(pixels, width, height, stride, region);
        }
        else
        {
            for (var index = 0; index < _uncovered.Count; index++)
            {
                backgroundPixels += Background.RenderRegion(pixels, width, height, stride, _uncovered[index]);
            }
        }

        long surfacePixels = 0;
        var surfaceCount = 0;
        lock (_gate)
        {
            foreach (var surface in OrderedSurfaces())
            {
                // Cursor surfaces are drawn at the pointer, never as windows;
                // minimised windows are off screen until the taskbar asks
                // for them back.
                if (!surface.IsMapped || surface.IsCursorSurface || surface.Hidden)
                {
                    continue;
                }

                var rect = WindowRect(surface, scale).Intersect(region);

                // The desktop was painted on the assumption that opaque
                // windows hide it. A window that was opaque in the pass
                // above and is translucent *now* — it committed a different
                // buffer since — has no desktop under it and must not blend
                // against the frame it is replacing. A window that was never
                // counted opaque already has the desktop under it, even when
                // its whole rect lies over another opaque window: painting
                // the background there would erase that window.
                if (!IsOpaqueWindow(surface) && WasOpaqueUnder(surface, out var wasOpaqueRect))
                {
                    var under = wasOpaqueRect.Intersect(region);
                    if (!under.IsEmpty)
                    {
                        backgroundPixels += Background.RenderRegion(pixels, width, height, stride, under);
                    }
                }

                surface.BlitInto(pixels, width, height, stride, scale, region);
                if (!rect.IsEmpty)
                {
                    surfacePixels += rect.Area;
                    surfaceCount++;
                }
            }

            // No chrome around windows: the toolkit owns its decorations
            // (and the taskbar's accent is the focus's visible consequence).
        }

        return new RenderCost(backgroundPixels, surfacePixels, surfaceCount);
    }

    /// <summary>
    /// Draws one of the theme's pointer images at the pointer: native
    /// physical pixels, its own hotspot, clipped to the damage region.
    /// </summary>
    private static void DrawTheme(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        int cursorX,
        int cursorY,
        int scale,
        CursorBitmap bitmap,
        in DamageRect region) =>
        Blitter.Blit(
            pixels, width, height, stride,
            MemoryMarshal.AsBytes(bitmap.Pixels.AsSpan()),
            bitmap.SourceWidth, bitmap.SourceHeight, bitmap.SourceWidth * 4,
            ShmFormats.Argb8888,
            (cursorX * scale) - bitmap.HotspotX,
            (cursorY * scale) - bitmap.HotspotY,
            bitmap.Width, bitmap.Height,
            region);

    /// <summary>
    /// Whether a region intersects the dock band or another open shell panel.
    /// </summary>
    private bool NeedsChrome(in DamageRect region, int scale, int width, int height)
    {
        if (Shell is not { } shell)
        {
            return false;
        }

        var bandTop = ((height / scale) - Rosella.Compositor.Shell.BarHeight) * scale;
        if (shell.DockVisible && region.Y + region.H > bandTop)
        {
            return true;
        }

        if (shell.Switcher.IsOpen)
        {
            return true;
        }

        if (shell.DockMenu is { } menu)
        {
            var bounds = menu.Bounds.Scaled(scale);
            if (region.Intersects(new DamageRect(bounds.X, bounds.Y, bounds.W, bounds.H)))
            {
                return true;
            }
        }

        if (!shell.Settings.IsOpen)
        {
            return false;
        }

        // The panel sits over the work area: a region that cannot reach it
        // does not need it redrawn (a video playing behind an open panel must
        // not pay for the panel every frame).
        if (shell.LastBarLayout is not { } layout)
        {
            return true;
        }

        var panel = shell.Settings.PanelRect(layout.Clock, width / scale, height / scale).Scaled(scale);
        return region.Intersects(new DamageRect(panel.X, panel.Y, panel.W, panel.H));
    }

    /// <summary>Whether the surface is a mapped opaque (xrgb) window: nothing shows through it.</summary>
    /// <summary>The rectangle the desktop pass skipped for this surface, if any.</summary>
    private bool WasOpaqueUnder(SurfaceHandler surface, out DamageRect rect)
    {
        foreach (var (candidate, skipped) in _opaqueRects)
        {
            if (ReferenceEquals(candidate, surface))
            {
                rect = skipped;
                return true;
            }
        }

        rect = default;
        return false;
    }

    private readonly List<(SurfaceHandler Surface, DamageRect Rect)> _opaqueRects = [];

    private static bool IsOpaqueWindow(SurfaceHandler surface) =>
        surface.IsMapped
        && !surface.Gone
        && !surface.IsCursorSurface
        && !surface.Hidden
        && surface.IsOpaqueContent
        && surface.ContentWidth > 0
        && surface.ContentHeight > 0;

    /// <summary>The physical rectangle a window's content is blitted into.</summary>
    private static DamageRect WindowRect(SurfaceHandler surface, int scale) =>
        new(surface.Position.X * scale, surface.Position.Y * scale, surface.Width * scale, surface.Height * scale);

    /// <summary>Fires frame callbacks for the frame just composited.</summary>
    public void CompleteFrame()
    {
        lock (_gate)
        {
            foreach (var surface in OrderedSurfaces())
            {
                // A minimised window gets no frame callbacks: that is the
                // throttle xdg-shell points clients at, and it keeps a hidden
                // video or spinner from redrawing for a screen it is not on.
                // Callbacks queued while hidden are not dropped — they fire
                // on the first frame after the restore, which is the frame
                // the client's new content is actually on screen (a burst of
                // "done" then is harmless: each was one-shot).
                if (surface.IsMapped && !surface.Hidden)
                {
                    surface.CompleteFrame();
                }
            }
        }
    }

    /// <summary>Test seam: the registered surfaces.</summary>
    internal IReadOnlyList<SurfaceHandler> Surfaces
    {
        get
        {
            lock (_gate)
            {
                return _surfaces.ToArray();
            }
        }
    }

    /// <summary>Enables a popup grab, keeping the popup stack in creation order.</summary>
    internal bool TryGrabPopup(XdgPopupHandler popup)
    {
        lock (_gate)
        {
            if (popup.RootParentSurface?.XdgRole?.Toplevel is not { } rootToplevel
                || rootToplevel.Surface.Session != popup.Surface.Session)
            {
                return false;
            }

            if (popup.ParentIsPopup && (popup.Parent is not { } parent || !_popupGrabs.Contains(parent)))
            {
                return false;
            }

            _popupGrabs.Add(popup);
            return true;
        }
    }

    /// <summary>Snapshot of popup grabs in nesting order.</summary>
    internal XdgPopupHandler[] PopupGrabs
    {
        get
        {
            lock (_gate)
            {
                return [.. _popupGrabs];
            }
        }
    }

    /// <summary>The current topmost grabbed popup, if any.</summary>
    internal XdgPopupHandler? TopmostPopupGrab
    {
        get
        {
            lock (_gate)
            {
                return _popupGrabs.Count > 0 ? _popupGrabs[^1] : null;
            }
        }
    }

    /// <summary>Releases a popup grab and any nested grabs above it.</summary>
    internal void ReleasePopupGrab(XdgPopupHandler popup)
    {
        XdgPopupHandler[] descendants;
        lock (_gate)
        {
            var index = _popupGrabs.IndexOf(popup);
            if (index < 0)
            {
                return;
            }

            descendants = _popupGrabs.Skip(index + 1).Reverse().ToArray();
            _popupGrabs.RemoveRange(index, _popupGrabs.Count - index);
        }

        foreach (var descendant in descendants)
        {
            descendant.ParentDismissed();
        }
    }

    /// <summary>Dismisses the current grab stack in reverse nesting order.</summary>
    internal void DismissPopupGrabs()
    {
        while (TopmostPopupGrab is { } popup)
        {
            popup.DismissFromCompositor();
        }
    }

    /// <summary>The topmost mapped surface at the point, if any.</summary>
    internal SurfaceHandler? HitTest(int x, int y)
    {
        lock (_gate)
        {
            var ordered = OrderedSurfaces().ToArray();
            if (_popupGrabs.Count > 0)
            {
                var popup = _popupGrabs[^1];
                var popupSurface = popup.Surface;
                for (var i = ordered.Length - 1; i >= 0; i--)
                {
                    var surface = ordered[i];
                    if (surface.Session != popupSurface.Session || !surface.IsMapped || surface.Gone || surface.IsCursorSurface
                        || surface.Hidden || !Contains(surface.Visible, x, y) || !surface.AcceptsInputAt(x, y))
                    {
                        continue;
                    }

                    return surface;
                }

                // An owner-events grab blocks unrelated clients from receiving
                // the press. The caller dismisses the grab on this null hit.
                return null;
            }

            for (var i = ordered.Length - 1; i >= 0; i--)
            {
                var surface = ordered[i];
                if (!surface.IsMapped || surface.IsCursorSurface || surface.Hidden)
                {
                    continue;
                }

                if (surface.AcceptsInputAt(x, y))
                {
                    return surface;
                }
            }

            return null;
        }
    }

    /// <summary>The taskbar's windows: every mapped toplevel, oldest first.</summary>
    internal IReadOnlyList<BarTask> TaskWindows()
    {
        lock (_gate)
        {
            var tasks = new List<BarTask>();
            foreach (var surface in _surfaces)
            {
                if (!surface.IsMapped || surface.Gone || surface.IsCursorSurface)
                {
                    continue;
                }

                if (surface.XdgRole?.Toplevel is { } toplevel)
                {
                    tasks.Add(new BarTask(
                        Bar.LabelFor(toplevel.Title, toplevel.AppId), toplevel.AppId, surface, surface.Hidden));
                }
            }

            return tasks;
        }
    }

    /// <summary>
    /// Hides a window (xdg_toplevel.set_minimized): no drawing, no hits, and
    /// the keyboard goes to the topmost window still on screen. The surface
    /// stays mapped with its buffer, and its taskbar button stays put — that
    /// is the way back, since xdg-shell gives the client no "unminimize".
    /// </summary>
    internal void Minimize(SurfaceHandler surface)
    {
        lock (_gate)
        {
            if (surface.Hidden || !surface.IsMapped)
            {
                return;
            }

            surface.Hidden = true;
            _damage.MarkFull();
            _dirty = true;
            Monitor.PulseAll(_gate);
        }

        // Snapshot the focus decision and the held keys before the popup
        // teardown: dismissing a grabbed menu moves focus back to this
        // window with an empty key set, and the handover must still carry
        // what the user is holding. Hiding an unfocused window takes no
        // focus with it, so it must not re-target anyone else's keyboard.
        var hadFocus = Seat?.KeyboardFocusIsWithin(surface) == true;
        var held = Seat?.HeldKeys ?? [];

        // A menu or tooltip belongs to a place on screen the window no longer
        // occupies.
        DismissDescendantPopups(surface);

        // Clears the seat's focus and sends leave while the surface id is
        // still valid, exactly like an unmap.
        Seat?.OnSurfaceHidden(surface, canSendLeave: true);

        if (hadFocus && TopmostVisible() is { } next)
        {
            Seat?.SetKeyboardFocus(next, held);
        }
    }

    /// <summary>Brings a minimised window back; the caller raises and focuses it.</summary>
    internal void Restore(SurfaceHandler surface)
    {
        lock (_gate)
        {
            if (!surface.Hidden)
            {
                return;
            }

            surface.Hidden = false;
            _damage.MarkFull();
            _dirty = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>The topmost window that is on screen and can take the keyboard.</summary>
    internal SurfaceHandler? TopmostVisible()
    {
        lock (_gate)
        {
            for (var i = _surfaces.Count - 1; i >= 0; i--)
            {
                var candidate = _surfaces[i];
                if (candidate.IsMapped && !candidate.Gone && !candidate.IsCursorSurface && !candidate.Hidden
                    && candidate.XdgRole?.Toplevel is not null)
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Dismisses every popup whose ancestor chain reaches the surface: a
    /// popup may never outlive its parent's place on screen.
    /// </summary>
    internal void DismissDescendantPopups(SurfaceHandler surface)
    {
        List<XdgSurfaceHandler> snapshot;
        lock (_gate)
        {
            snapshot = _surfaces
                .Where(candidate => candidate.XdgRole?.PopupParent is not null)
                .Select(candidate => candidate.XdgRole!)
                .OrderByDescending(role => PopupDepth(role))
                .ToList();
        }

        foreach (var role in snapshot)
        {
            var parent = role.PopupParent;
            while (parent is not null)
            {
                if (parent.Surface == surface)
                {
                    role.DismissPopup();
                    break;
                }

                parent = parent.PopupParent;
            }
        }
    }

    private static int PopupDepth(XdgSurfaceHandler role)
    {
        var depth = 0;
        while (role.PopupParent is { } parent)
        {
            depth++;
            role = parent;
        }

        return depth;
    }

    private static bool Contains((int X, int Y, int W, int H) rect, int x, int y) =>
        x >= rect.X && x < rect.X + rect.W && y >= rect.Y && y < rect.Y + rect.H;

    /// <summary>Raises the surface to the top (it renders last).</summary>
    internal void Raise(SurfaceHandler surface)
    {
        surface = surface.MainSurface;
        lock (_gate)
        {
            if (_surfaces.Remove(surface))
            {
                _surfaces.Add(surface);
                _damage.MarkFull();
                _dirty = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>Cascade placement for surfaces with no role (raw test clients).</summary>
    private static (int X, int Y) Cascade(int index)
    {
        var offset = (index % 10) * 72;
        return (64 + offset, 64 + offset);
    }
}
