namespace Rosella.Compositor;

/// <summary>
/// The taskbar's view of the session: the windows an app launcher can
/// claim, and the two things a click may do to them — raise and focus, or
/// launch the app. Production is <see cref="SceneWindowRegistry"/>; tests
/// fake it.
/// </summary>
internal interface IWindowRegistry
{
    /// <summary>The topmost mapped window in z-order belonging to the app, if any.</summary>
    SurfaceHandler? MostRecentMatching(IReadOnlyList<string> matchKeys);

    /// <summary>Queues recovery of a stranded ChatGPT window, if one exists.</summary>
    bool TryRecoverChatGpt(IReadOnlyList<string> matchKeys, Action relaunch, Action? failed) => false;

    /// <summary>Raises the window and hands it the keyboard.</summary>
    void RaiseAndFocus(SurfaceHandler surface, IReadOnlyList<int> heldKeys);

    /// <summary>
    /// Launches the app's argv through the shared spawn helper and reports
    /// the new process's pid (null when nothing started), so the shell can
    /// spin the launcher until the window shows up.
    /// </summary>
    int? Launch(IReadOnlyList<string> argv);
}

/// <summary>The real registry: the scene's mapped toplevels and the seat's keyboard.</summary>
internal sealed class SceneWindowRegistry : IWindowRegistry
{
    private static readonly HashSet<int> RecoveringPeers = [];
    private readonly Scene _scene;
    private readonly SeatState _seat;
    private readonly string? _socketName;
    internal XWaylandBridge? XWayland { get; set; }

    internal SceneWindowRegistry(Scene scene, SeatState seat, string? socketName)
    {
        _scene = scene;
        _seat = seat;
        _socketName = socketName;
    }

    public SurfaceHandler? MostRecentMatching(IReadOnlyList<string> matchKeys)
    {
        var surfaces = _scene.Surfaces;
        for (var i = surfaces.Count - 1; i >= 0; i--)
        {
            var surface = surfaces[i];
            if (!surface.IsMapped || surface.Gone || surface.IsCursorSurface)
            {
                continue;
            }

            if (surface.XdgRole?.Toplevel is { } toplevel && PinnedMatch.Matches(toplevel.AppId, matchKeys))
            {
                return surface;
            }
        }

        return null;
    }

    public bool TryRecoverChatGpt(IReadOnlyList<string> matchKeys, Action relaunch, Action? failed)
    {
        var other = _scene.OtherDesktop;
        var candidates = other is null ? _scene.Surfaces : _scene.Surfaces.Concat(other.Surfaces);
        foreach (var surface in candidates.Reverse())
        {
            var episode = surface.UnmapVersion;
            if (episode == 0 || surface.Gone || surface.Session is not { PeerPid: > 0 } session ||
                surface.XdgRole is not { IsDefunct: false, Toplevel: { } top } ||
                !PinnedMatch.Matches(top.AppId, matchKeys))
            {
                continue;
            }

            // The taskbar can be clicked again while the worker waits for exit.
            lock (RecoveringPeers)
            {
                if (!RecoveringPeers.Add(session.PeerPid))
                {
                    return true;
                }
            }

            _ = Task.Run(() =>
            {
                try
                {
                    RecoverChatGpt(surface, episode, matchKeys, relaunch, failed);
                }
                finally
                {
                    lock (RecoveringPeers)
                    {
                        RecoveringPeers.Remove(session.PeerPid);
                    }
                }
            });
            return true;
        }

        return false;
    }

    private void RecoverChatGpt(SurfaceHandler surface, long episode, IReadOnlyList<string> matchKeys, Action relaunch, Action? failed)
    {
        var session = surface.Session!;
        using var process = session.OpenPeerProcess();
        var other = _scene.OtherDesktop ?? _scene;
        var fallback = false;
        var signaled = false;
        var shouldReportFailure = false;

        // Recheck on both desktops under the same locks used for buffer commits
        // and window moves, then signal the verified peer before releasing them.
        Scene.WithBoth(_scene, other, () =>
        {
            if (HasMappedWindow(_scene, session.PeerPid, matchKeys) ||
                (other != _scene && HasMappedWindow(other, session.PeerPid, matchKeys)))
            {
                return;
            }

            if (surface.Gone || process is null || ClientProcess.HasExited(process) ||
                !ClientProcess.IsChatGpt(session.PeerPid))
            {
                fallback = true;
                return;
            }

            if (surface.UnmapVersion == episode &&
                surface.XdgRole is { IsDefunct: false, Toplevel: { } top } &&
                PinnedMatch.Matches(top.AppId, matchKeys))
            {
                signaled = ClientProcess.Terminate(process);
                shouldReportFailure = !signaled;
            }
        });

        if (fallback || (signaled && ClientProcess.HasExited(process!, timeoutMilliseconds: 2000)))
        {
            if (signaled)
            {
                Console.WriteLine("pinned: restarting ChatGPT after its last window unmapped");
            }
            relaunch();
        }
        else if (signaled || shouldReportFailure)
        {
            Console.Error.WriteLine("pinned: ChatGPT did not exit after requesting recovery");
            failed?.Invoke();
        }
    }

    private static bool HasMappedWindow(Scene scene, int peerPid, IReadOnlyList<string> matchKeys) =>
        scene.Surfaces.Any(surface => surface is { IsMapped: true, Gone: false, IsCursorSurface: false,
            XdgRole.Toplevel: { } top } &&
            (surface.Session?.PeerPid == peerPid || PinnedMatch.Matches(top.AppId, matchKeys)));

    public void RaiseAndFocus(SurfaceHandler surface, IReadOnlyList<int> heldKeys)
    {
        // A switcher snapshot can outlive the selected window's mapping or
        // role. Never send keyboard enter to a surface that cannot receive it.
        if (surface.Gone || !surface.IsMapped || surface.XdgRole?.Toplevel is null)
        {
            return;
        }

        // A pinned launcher or Alt+Tab can land on a window that was
        // minimised; restoring is part of focusing it.
        _scene.Restore(surface);
        _scene.Raise(surface);
        _seat.SetKeyboardFocus(surface, heldKeys);
    }

    public int? Launch(IReadOnlyList<string> argv) => AppLaunch.Spawn(argv, _socketName, XWayland?.Current);
}
