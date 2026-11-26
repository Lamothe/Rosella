namespace Rosella.Compositor;

/// <summary>Shared paint inputs. The dashboard owns user state independently of a widget's loaded code.</summary>
internal readonly record struct DashboardWidgetFrame(
    int Width, int Height, int Stride, int Scale, DamageRect Damage, DashboardLayout Layout, (int X, int Y)? Pointer,
    DashboardAppsFrame Apps, WeatherView Weather, DashboardCalendar Calendar,
    NotificationRecord[] Notifications, int NotificationOffset, ISettingsPanel Settings, Rect SettingsPanel);

internal readonly record struct DashboardAppsFrame(
    DashboardApp[]? Items, int FirstRow, int SelectedIndex, DashboardPinMenu? Menu, Func<string, int, IconImage?> Icon);

internal sealed record DashboardPinMenu(Rect Bounds, string Id, bool Pinned);

internal interface IDashboardWidget
{
    void Draw(Span<byte> pixels, in DashboardWidgetFrame frame);
}

internal interface IDashboardSettingsWidget : IDashboardWidget
{
    ISettingsPanel CreateSettings(ISystemActions actions, IVolumeControl volume);
}
