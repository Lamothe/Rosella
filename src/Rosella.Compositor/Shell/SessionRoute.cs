namespace Rosella.Compositor;

/// <summary>
/// The two lines in the control state that say where this session shows
/// and whether it is the one on screen. The sudo askpass helper greps
/// these to aim its dialog at the session the person is looking at — so
/// their format is a contract, not decoration.
/// </summary>
internal static class SessionRoute
{
    /// <summary>Null when the session has no display name to report.</summary>
    internal static string? DisplayLine(string display) =>
        display.Length == 0 ? null : $"display {display}";

    /// <summary>Null when there is no VT (e.g. running over SSH).</summary>
    internal static string? VtLine((int Number, bool Active)? vt) =>
        vt is not { } state || state.Number <= 0
            ? null
            : $"vt {state.Number} {(state.Active ? "active" : "inactive")}";
}
