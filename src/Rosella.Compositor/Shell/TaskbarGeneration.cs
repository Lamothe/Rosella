using System.Runtime.Loader;
using System.Security.Cryptography;

namespace Rosella.Compositor;

internal sealed record TaskbarGeneration(ITaskbarModule Module, ISettingsPanel Settings, AssemblyLoadContext? Context, string? BuildId = null)
{
    internal const string AssemblyFile = "Rosella.Taskbar.dll";
    internal string BuildLabel => BuildId is { } id ? $"Build {id[..12]}" : "Built-in";

    // Keep the previous contract's module available to a compositor that
    // is still running while a full release is published for its next login.
    internal static string PublishedPath(string directory) => Path.Combine(directory, "modules", "v3", AssemblyFile);

    internal static string InstalledBuild(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    internal static TaskbarGeneration Load(string path, ISystemActions systemActions, IVolumeControl volume)
    {
        // Share the compositor's contracts and services with the module. A
        // separate context lets the previous generation retire after any paint
        // or audio operation using it has finished.
        var context = new AssemblyLoadContext("Rosella.Taskbar", isCollectible: true);
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var file = new MemoryStream(bytes, writable: false);
            var assembly = context.LoadFromStream(file);
            var entry = assembly.GetType("Rosella.Taskbar.TaskbarImplementation", throwOnError: true)!;
            var module = (ITaskbarModule)Activator.CreateInstance(entry)!;
            return new TaskbarGeneration(module, module.CreateSettings(systemActions, volume), context,
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch
        {
            context.Unload();
            throw;
        }
    }
}
