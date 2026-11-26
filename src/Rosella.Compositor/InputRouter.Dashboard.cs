using Rosella.Input;

namespace Rosella.Compositor;

internal sealed partial class InputRouter
{
    private DashboardLayout CurrentDashboardLayout() => DashboardLayout.Create(_scene.OutputSize.Width, _scene.OutputSize.Height);

    private void ToggleDashboard(Shell shell, DashboardPage page = DashboardPage.Applications)
    {
        if (shell.Dashboard.IsOpen)
        {
            CloseDashboard(shell);
            return;
        }

        _scene.DismissPopupGrabs();
        shell.Switcher.Cancel();
        shell.DockMenu = null;
        shell.TaskbarPress = null;
        shell.Settings.Close();
        shell.Settings.Toggle();
        shell.Dashboard.Open(page);
        var previous = _seat.PointerFocus;
        _seat.SetPointerFocus(null, 0, 0);
        if (previous is not null)
        {
            _seat.SendPointerFrame(previous);
        }
        _scene.MarkDirty();
    }

    private void CloseDashboard(Shell shell)
    {
        shell.Dashboard.Close();
        shell.Settings.Close();
        shell.UpdateDockReveal(_cursorY, _scene.OutputSize.Height);
        var target = PointerOverShell() ? null : _scene.HitTest(_cursorX, _cursorY);
        _seat.SetPointerFocus(target, LocalX(target), LocalY(target));
        if (target is not null)
        {
            _seat.SendPointerFrame(target);
        }
        _scene.MarkDirty();
    }

    private void MoveOverDashboard(Shell shell, (int X, int Y) previous, (int X, int Y) current)
    {
        var dashboard = shell.Dashboard;
        var layout = CurrentDashboardLayout();
        var scale = _scene.Scale;
        var oldTile = dashboard.HoverTile(layout, previous);
        var newTile = dashboard.HoverTile(layout, current);
        if (oldTile != newTile)
        {
            var changed = oldTile.ToDamage(scale).Union(newTile.ToDamage(scale));
            _scene.MarkDirty(changed);
        }
        if (dashboard.HitMenu(previous) != dashboard.HitMenu(current))
        {
            _scene.MarkDirty(dashboard.MenuBounds.ToDamage(scale));
        }
        if (dashboard.IsInWeatherCredit(layout, previous.X, previous.Y) != dashboard.IsInWeatherCredit(layout, current.X, current.Y))
        {
            _scene.MarkDirty(layout.Weather.ToDamage(scale));
        }
        if (dashboard.CalendarHover(layout, previous) != dashboard.CalendarHover(layout, current))
        {
            _scene.MarkDirty(layout.Calendar.ToDamage(scale));
        }

        if (!layout.Compact || dashboard.Page == DashboardPage.Settings)
        {
            var panel = dashboard.SettingsPanel(layout, shell.Settings);
            shell.Settings.PointerMoved(_cursorX, panel);
            var viewport = Dashboard.SettingsViewport(layout);
            if (viewport.Contains(previous.X, previous.Y) || viewport.Contains(current.X, current.Y) ||
                shell.Settings.IsDraggingSlider)
            {
                _scene.MarkDirty(viewport.ToDamage(scale));
            }
        }

        if (shell.DockMenu is { } menu && (menu.Bounds.Contains(previous.X, previous.Y) || menu.Bounds.Contains(current.X, current.Y)))
        {
            _scene.MarkDirty(menu.Bounds.ToDamage(scale));
        }
    }

    // The dock keeps its existing hit testing and drag behavior.
    private bool PressDashboard(Shell shell, int button)
    {
        var dashboard = shell.Dashboard;
        var layout = CurrentDashboardLayout();
        var left = button == InputConstants.ButtonLeft;
        if (dashboard.MenuOpen)
        {
            if (dashboard.DismissMenu(_cursorX, _cursorY, left) is { } id)
            {
                UpdatePins(shell, "Could not add app to taskbar", other => shell.AddPin(id, other));
            }
            _chromePressConsumedButtons.Add(button);
            return true;
        }

        var panel = dashboard.SettingsPanel(layout, shell.Settings);
        if ((!layout.Compact || dashboard.Page == DashboardPage.Settings) &&
            ((Dashboard.SettingsViewport(layout).Contains(_cursorX, _cursorY) && panel.Contains(_cursorX, _cursorY))
                || shell.Settings.IsDraggingSlider))
        {
            if (left)
            {
                shell.Settings.HandlePointer(_cursorX, _cursorY, panel, pressed: true);
                if (!shell.Settings.IsOpen)
                {
                    CloseDashboard(shell);
                }
            }
            _settingsPressConsumedButtons.Add(button);
            return true;
        }

        var (width, height) = _scene.OutputSize;
        var bar = shell.LastBarLayout ?? shell.ComputeBarLayout(width, height, _scene.TaskWindows());
        if (bar.Bounds.Contains(_cursorX, _cursorY))
        {
            return false;
        }

        _chromePressConsumedButtons.Add(button);
        if (left && dashboard.TrySelectTab(layout, _cursorX, _cursorY))
        {
            return true;
        }
        if (left && dashboard.PressCalendar(layout, _cursorX, _cursorY))
        {
            return true;
        }
        if (dashboard.AppAt(layout, _cursorX, _cursorY) is { } app)
        {
            if (button == InputConstants.ButtonRight)
            {
                dashboard.OpenMenu(layout, _cursorX, _cursorY, app, shell.Pinned.Any(pin => pin.DesktopId == app.Id));
            }
            else if (left)
            {
                LaunchDashboardApp(shell, app);
            }
            return true;
        }
        if (left && dashboard.IsInWeatherCredit(layout, _cursorX, _cursorY))
        {
            CloseDashboard(shell);
            shell.Windows?.Launch(["xdg-open", "https://open-meteo.com/"]);
            return true;
        }
        if (!layout.Glass.Contains(_cursorX, _cursorY))
        {
            CloseDashboard(shell);
        }

        return true;
    }

    private void HandleDashboardKey(Shell shell, ushort key)
    {
        var dashboard = shell.Dashboard;
        var layout = CurrentDashboardLayout();
        switch (key)
        {
            case InputConstants.KeyEsc:
                if (dashboard.MenuOpen)
                {
                    dashboard.DismissMenu(0, 0, activate: false);
                }
                else if (layout.Compact && dashboard.Page != DashboardPage.Settings
                    || !shell.Settings.HandleKey(key) || !shell.Settings.IsOpen)
                {
                    CloseDashboard(shell);
                }
                break;
            case InputConstants.KeyUp:
                dashboard.MoveSelection(layout, -layout.Columns);
                break;
            case InputConstants.KeyDown:
                dashboard.MoveSelection(layout, layout.Columns);
                break;
            case InputConstants.KeyLeft:
                dashboard.MoveSelection(layout, -1);
                break;
            case InputConstants.KeyRight:
                dashboard.MoveSelection(layout, 1);
                break;
            case InputConstants.KeyEnter when (!layout.Compact || dashboard.Page == DashboardPage.Applications) &&
                dashboard.SelectedApp is { } app:
                LaunchDashboardApp(shell, app);
                break;
            case InputConstants.KeyTab when layout.Compact:
                var step = ShiftDown ? DashboardLayout.TabCount - 1 : 1;
                dashboard.SelectPage((DashboardPage)(((int)dashboard.Page + step) % DashboardLayout.TabCount));
                break;
        }
    }

    private void LaunchDashboardApp(Shell shell, DashboardApp app)
    {
        var launcher = shell.Pinned.FirstOrDefault(pin => pin.DesktopId == app.Id) ?? shell.ResolvePin?.Invoke(app.Id);
        if (launcher is null || shell.Windows is not { } windows)
        {
            shell.Notifications.Show(default, "Rosella", "Could not open " + app.Name, "Its launcher is unavailable.", 4000);
            return;
        }
        CloseDashboard(shell);
        shell.Spinner.Start(launcher, launcher.Click(windows, [.. _pressedKeys]));
    }
}
