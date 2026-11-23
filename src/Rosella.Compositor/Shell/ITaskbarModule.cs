namespace Rosella.Compositor;

internal interface ITaskbarModule
{
    string ClockString();

    BarLayout ComputeLayout(int width, int height, string clock, IReadOnlyList<BarTask> tasks,
        IReadOnlyList<PinnedLauncher> pinned);

    void Draw(Span<byte> pixels, int width, int height, int stride, int scale, BarLayout layout,
        string clock, SurfaceHandler? focused, (int X, int Y)? pointer, int pressedPin, FrostedGlass glass);

    ISettingsPanel CreateSettings(ISystemActions systemActions, IVolumeControl volume);
}

internal interface ISettingsPanel
{
    ResolutionPicker? Resolution { get; set; }
    Action? Dirty { get; set; }
    TaskbarModuleControls? Modules { get; set; }
    bool ModulesOpen { get; }
    void OpenModules();
    bool IsOpen { get; }
    bool IsDraggingSlider { get; }
    void HandleVolumeKey(ushort code, int value);
    void Toggle();
    void Close();
    bool HandleKey(ushort key);
    Rect PanelRect(Rect clock, int outputWidth, int outputHeight);
    bool HandlePointer(int x, int y, Rect clock, int outputWidth, int outputHeight, bool pressed);
    void PointerReleased();
    void PointerMoved(int x, Rect clock, int outputWidth, int outputHeight);
    void Draw(Span<byte> pixels, int stride, int scale, Rect panel, (int X, int Y)? pointer, FrostedGlass? glass);
}

internal sealed record TaskbarModuleControls(Func<(string Build, string? Result)> ReadStatus, Action Reload);
