using System.Runtime.InteropServices;

namespace Rosella.GpuProbe;

/// <summary>
/// A GBM device on a render node plus the EGL display, config and context
/// built on it. Creation is entirely non-privileged: no DRM master, no
/// scanout, no modeset.
/// </summary>
internal sealed unsafe class GpuContext : IDisposable
{
    private bool _disposed;

    private GpuContext(int fd, string renderNode, nint gbmDevice, nint display, nint config, nint context)
    {
        Fd = fd;
        RenderNode = renderNode;
        GbmDevice = gbmDevice;
        Display = display;
        Config = config;
        Context = context;
        CreateImage = Load<Egl.CreateImageKhr>("eglCreateImageKHR");
        DestroyImage = Load<Egl.DestroyImageKhr>("eglDestroyImageKHR");
        ImageTargetTexture = Load<Egl.ImageTargetTexture2DOes>("glEGLImageTargetTexture2DOES");
    }

    internal int Fd { get; }

    internal string RenderNode { get; }

    internal nint GbmDevice { get; }

    internal nint Display { get; }

    internal nint Config { get; }

    internal nint Context { get; }

    internal Egl.CreateImageKhr? CreateImage { get; }

    internal Egl.DestroyImageKhr? DestroyImage { get; }

    internal Egl.ImageTargetTexture2DOes? ImageTargetTexture { get; }

    internal string BackendName => Marshal.PtrToStringUTF8(Gbm.DeviceGetBackendName(GbmDevice)) ?? "(unknown)";

    internal string EglVendor => Query(Egl.Vendor);

    internal string EglVersion => Query(Egl.Version);

    internal string EglClientApis => Query(Egl.ClientApis);

    internal string EglExtensions => Query(Egl.Extensions);

    /// <summary>Creates the GBM device and an ES 2.0 context on <paramref name="renderNode"/>.</summary>
    internal static GpuContext Create(string renderNode)
    {
        var fd = Libc.Open(renderNode, Libc.O_RdWr | Libc.OCloExec);
        if (fd < 0)
        {
            throw new ProbeException($"open({renderNode}) failed: {Libc.ErrorName(Marshal.GetLastPInvokeError())}");
        }

        var gbm = Gbm.CreateDevice(fd);
        if (gbm == 0)
        {
            Libc.Close(fd);
            throw new ProbeException("gbm_create_device failed");
        }

        var display = CreateDisplay(gbm);
        if (display == 0)
        {
            Gbm.DeviceDestroy(gbm);
            Libc.Close(fd);
            throw new ProbeException("eglGetPlatformDisplay(EGL_PLATFORM_GBM_KHR) returned EGL_NO_DISPLAY");
        }

        if (Egl.Initialize(display, out var major, out var minor) == 0)
        {
            throw new ProbeException($"eglInitialize failed: {Egl.Describe(Egl.GetError())}");
        }

        if (!HasSurfaceless(QueryOn(display, Egl.Extensions)))
        {
            throw new ProbeException("EGL_KHR_surfaceless_context is absent: a surfaceless GPU probe is not possible here");
        }

        var config = ChooseConfig(display, Egl.OpenGlEs2Bit);
        if (config == 0)
        {
            throw new ProbeException($"no ES2 EGLConfig: {Egl.Describe(Egl.GetError())}");
        }

        if (Egl.BindApi(Egl.OpenGlEsApi) == 0)
        {
            throw new ProbeException($"eglBindAPI(EGL_OPENGL_ES_API) failed: {Egl.Describe(Egl.GetError())}");
        }

        var context = CreateContext(display, config, clientVersion: 2);
        if (context == 0)
        {
            throw new ProbeException($"eglCreateContext(ES2) failed: {Egl.Describe(Egl.GetError())}");
        }

        if (Egl.MakeCurrent(display, 0, 0, context) == 0)
        {
            throw new ProbeException($"eglMakeCurrent(surfaceless) failed: {Egl.Describe(Egl.GetError())}");
        }

        Console.Error.WriteLine($"egl: EGL {major}.{minor}, {QueryOn(display, Egl.Version)}");
        return new GpuContext(fd, renderNode, gbm, display, config, context);
    }

    /// <summary>An ES context of a requested client version, for capability reporting.</summary>
    internal (nint Context, string Vendor, string Renderer, string Version, string ShadingLanguage) ProbeEs(int clientVersion)
    {
        if (Egl.BindApi(Egl.OpenGlEsApi) == 0)
        {
            return (0, "", "", "", $"eglBindAPI(EGL_OPENGL_ES_API) failed: {Egl.Describe(Egl.GetError())}");
        }

        int[] attribs = [Egl.ContextClientVersion, clientVersion, Egl.None];
        return ProbeContext(Egl.OpenGlEs2Bit, attribs, $"ES {clientVersion}");
    }

    /// <summary>
    /// A desktop GL context, for reporting only. Tries core 4.5, then core
    /// 3.3, then a default context: some Mesa builds reject the profile
    /// attribute even when the driver supports the generation.
    /// </summary>
    internal (nint Context, string Vendor, string Renderer, string Version, string ShadingLanguage) ProbeDesktopGl()
    {
        if (Egl.BindApi(Egl.OpenGlApi) == 0)
        {
            return (0, "", "", "", $"eglBindAPI(EGL_OPENGL_API) failed: {Egl.Describe(Egl.GetError())}");
        }

        (int Major, int Minor, bool Core)[] attempts = [(4, 5, true), (3, 3, true), (0, 0, false)];
        (nint Context, string Vendor, string Renderer, string Version, string ShadingLanguage) last = (0, "", "", "", "no attempt made");
        foreach (var (major, minor, core) in attempts)
        {
            int[] attribs = major == 0
                ? [Egl.None]
                : [Egl.ContextMajorVersion, major, Egl.ContextMinorVersion, minor, Egl.ContextOpenGlProfileMask, Egl.ContextOpenGlCoreProfileBit, Egl.None];
            var result = ProbeContext(Egl.OpenGlBit, attribs, $"desktop GL {major}.{minor}");
            if (result.Context != 0)
            {
                return result;
            }

            last = result;
        }

        // The default-context attempt returns a context that is never core;
        // drop the profile attribute for it.
        int[] fallback = [Egl.None];
        var plain = ProbeContext(Egl.OpenGlBit, fallback, "desktop GL default");
        return plain.Context != 0 ? plain : last;
    }

    private (nint Context, string Vendor, string Renderer, string Version, string ShadingLanguage) ProbeContext(uint renderableBit, int[] attribs, string label)
    {
        var config = ChooseConfig(Display, renderableBit);
        if (config == 0)
        {
            return (0, "", "", "", $"no EGLConfig for {label}");
        }

        fixed (int* pointer = attribs)
        {
            var context = Egl.CreateContext(Display, config, 0, pointer);
            if (context == 0)
            {
                return (0, "", "", "", $"eglCreateContext({label}) failed: {Egl.Describe(Egl.GetError())}");
            }

            try
            {
                if (Egl.MakeCurrent(Display, 0, 0, context) == 0)
                {
                    return (0, "", "", "", $"eglMakeCurrent({label}) failed: {Egl.Describe(Egl.GetError())}");
                }

                var getString = Load<Egl.GetStringFn>("glGetString");
                if (getString is null)
                {
                    return (0, "", "", "", "eglGetProcAddress(glGetString) returned NULL");
                }

                return (context, Str(getString(Gles.Vendor)), Str(getString(Gles.Renderer)), Str(getString(Gles.VersionString)), Str(getString(Gles.ShadingLanguageVersion)));
            }
            finally
            {
                Egl.MakeCurrent(Display, 0, 0, 0);
            }
        }
    }

    /// <summary>The ES 2.0 context's strings, read through the ES entry points.</summary>
    internal (string Vendor, string Renderer, string Version, string ShadingLanguage) EsInfo()
        => (Str(Gles.GetString(Gles.Vendor)), Str(Gles.GetString(Gles.Renderer)), Str(Gles.GetString(Gles.VersionString)), Str(Gles.GetString(Gles.ShadingLanguageVersion)));

    internal static string Str(nint value) => value == 0 ? "(null)" : Marshal.PtrToStringUTF8(value) ?? "(unreadable)";

    internal static T? Load<T>(string name) where T : Delegate
    {
        var address = Egl.GetProcAddress(name);
        return address == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static nint CreateDisplay(nint gbm)
    {
        var platformDisplay = Egl.GetPlatformDisplay(Egl.PlatformGbmKhr, gbm, null);
        if (platformDisplay != 0)
        {
            return platformDisplay;
        }

        var extension = Load<Egl.AddPlatformDisplayExt>("eglGetPlatformDisplayEXT");
        return extension is null ? 0 : extension(Egl.PlatformGbmKhr, gbm, null);
    }

    private string Query(int name) => QueryOn(Display, name);

    private static string QueryOn(nint display, int name) => Str(Egl.QueryString(display, name));

    private static nint ChooseConfig(nint display, uint renderableBit)
    {
        int[] attribs =
        [
            Egl.SurfaceType, Egl.WindowBit,
            Egl.RenderableType, unchecked((int)renderableBit),
            Egl.RedSize, 8,
            Egl.GreenSize, 8,
            Egl.BlueSize, 8,
            Egl.AlphaSize, 8,
            Egl.None,
        ];

        fixed (int* pointer = attribs)
        {
            nint config;
            var count = 0;
            if (Egl.ChooseConfig(display, pointer, &config, 1, &count) == 0 || count == 0)
            {
                return 0;
            }

            return config;
        }
    }

    private static nint CreateContext(nint display, nint config, int clientVersion)
    {
        int[] attribs = [Egl.ContextClientVersion, clientVersion, Egl.None];
        fixed (int* pointer = attribs)
        {
            return Egl.CreateContext(display, config, 0, pointer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Egl.MakeCurrent(Display, 0, 0, 0);
        if (Context != 0)
        {
            Egl.DestroyContext(Display, Context);
        }

        Egl.Terminate(Display);
        Gbm.DeviceDestroy(GbmDevice);
        Libc.Close(Fd);
    }

    /// <summary>EGL/GL extension strings are space separated.</summary>
    internal static bool HasExtension(string extensions, string name)
        => extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(name, StringComparer.Ordinal);

    internal static bool HasSurfaceless(string extensions) => HasExtension(extensions, "EGL_KHR_surfaceless_context");
}

/// <summary>A probe precondition that is missing rather than a bug: reported, not thrown at the user.</summary>
internal sealed class ProbeException(string message) : Exception(message);