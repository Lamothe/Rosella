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
    bool HandlePointer(int x, int y, Rect panel, bool pressed);
    void PointerReleased();
    void PointerMoved(int x, Rect clock, int outputWidth, int outputHeight);
    void PointerMoved(int x, Rect panel);
    void Draw(Span<byte> pixels, int stride, int scale, Rect panel, (int X, int Y)? pointer, FrostedGlass? glass);
    void DrawControls(Span<byte> pixels, int stride, int scale, Rect panel, (int X, int Y)? pointer);
}

internal sealed record ModuleReloadChoice(string Label, Func<(string Build, string? Result)> ReadStatus, Action Reload);

internal sealed class TaskbarModuleControls(IReadOnlyList<ModuleReloadChoice> choices)
{
    private volatile int _selected;

    internal TaskbarModuleControls(Func<(string Build, string? Result)> readStatus, Action reload)
        : this([new ModuleReloadChoice("Taskbar & Quick Settings", readStatus, reload)]) { }

    internal ModuleReloadChoice Selected => choices[_selected];
    internal bool HasChoices => choices.Count > 1;
    internal (string Build, string? Result) ReadStatus() => Selected.ReadStatus();
    internal void Step(int direction) => _selected = (_selected + direction + choices.Count) % choices.Count;
}
