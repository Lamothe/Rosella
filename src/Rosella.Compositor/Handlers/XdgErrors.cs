namespace Rosella.Compositor;

/// <summary>
/// xdg-shell error codes, as defined by the protocol's per-interface enums.
/// </summary>
internal static class XdgErrors
{
    // xdg_wm_base.error
    internal const uint Role = 0;
    internal const uint DefunctSurfaces = 1;
    internal const uint InvalidPopupParent = 3;
    internal const uint InvalidPositioner = 5;

    // xdg_surface.error
    internal const uint NotConstructed = 1;
    internal const uint AlreadyConstructed = 2;
    internal const uint UnconfiguredBuffer = 3;
    internal const uint InvalidSerial = 4;
    internal const uint InvalidSize = 5;
    internal const uint DefunctRoleObject = 6;

    // xdg_toplevel.error
    internal const uint InvalidResizeEdge = 0;
    internal const uint InvalidParent = 1;
    internal const uint ToplevelInvalidSize = 2;

    // xdg_positioner.error
    internal const uint InvalidInput = 0;
}
