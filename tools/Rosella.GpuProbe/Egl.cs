using System.Runtime.InteropServices;

namespace Rosella.GpuProbe;

/// <summary>libEGL: the GBM platform display, contexts, and the dma-buf image import extension.</summary>
internal static unsafe class Egl
{
    private const string Lib = "libEGL.so.1";

    internal const uint Success = 0x3000;
    internal const uint NotInitialized = 0x3001;
    internal const uint BadAccess = 0x3002;
    internal const uint BadAlloc = 0x3003;
    internal const uint BadAttribute = 0x3004;
    internal const uint BadConfig = 0x3005;
    internal const uint BadContext = 0x3006;
    internal const uint BadCurrentSurface = 0x3007;
    internal const uint BadDisplay = 0x3008;
    internal const uint BadMatch = 0x3009;
    internal const uint BadNativePixmap = 0x300A;
    internal const uint BadNativeWindow = 0x300B;
    internal const uint BadParameter = 0x300C;
    internal const uint BadSurface = 0x300D;
    internal const uint ContextLost = 0x300E;
    internal const uint BadAccessKind = 0x300F;

    internal const int None = 0x3038;
    internal const int Vendor = 0x3053;
    internal const int Version = 0x3054;
    internal const int Extensions = 0x3055;
    internal const int ClientApis = 0x308D;

    internal const int SurfaceType = 0x3033;
    internal const int WindowBit = 0x0004;
    internal const int PbufferBit = 0x0001;
    internal const int RenderableType = 0x3040;
    internal const int OpenGlEs2Bit = 0x0004;
    internal const int OpenGlEs3Bit = 0x0040;
    internal const int OpenGlBit = 0x0008;

    internal const int RedSize = 0x3024;
    internal const int GreenSize = 0x3023;
    internal const int BlueSize = 0x3022;
    internal const int AlphaSize = 0x3021;
    internal const int DepthSize = 0x3025;

    internal const int ContextClientVersion = 0x3098;
    internal const int ContextMajorVersion = 0x3098;
    internal const int ContextMinorVersion = 0x3099;
    internal const int ContextOpenGlProfileMask = 0x30FD;
    internal const int ContextOpenGlCoreProfileBit = 0x0001;

    internal const uint OpenGlEsApi = 0x30A0;
    internal const uint OpenGlApi = 0x30A2;

    internal const uint PlatformGbmKhr = 0x31D7;
    internal const uint NoContext = 0;

    // EGL_EXT_image_dma_buf_import
    internal const uint LinuxDmaBufExt = 0x3270;
    internal const int LinuxDrmFourccExt = 0x3271;
    internal const int DmaBufPlane0FdExt = 0x3272;
    internal const int DmaBufPlane0OffsetExt = 0x3273;
    internal const int DmaBufPlane0PitchExt = 0x3274;
    internal const int DmaBufPlane0ModifierLoExt = 0x3443;
    internal const int DmaBufPlane0ModifierHiExt = 0x3444;
    internal const int Width = 0x3057;
    internal const int Height = 0x3056;

    [DllImport(Lib, EntryPoint = "eglGetPlatformDisplay")]
    internal static extern nint GetPlatformDisplay(uint platform, nint nativeDisplay, int* attribs);

    [DllImport(Lib, EntryPoint = "eglGetError")]
    internal static extern uint GetError();

    [DllImport(Lib, EntryPoint = "eglInitialize")]
    internal static extern uint Initialize(nint display, out int major, out int minor);

    [DllImport(Lib, EntryPoint = "eglTerminate")]
    internal static extern uint Terminate(nint display);

    [DllImport(Lib, EntryPoint = "eglQueryString")]
    internal static extern nint QueryString(nint display, int name);

    [DllImport(Lib, EntryPoint = "eglBindAPI")]
    internal static extern uint BindApi(uint api);

    [DllImport(Lib, EntryPoint = "eglChooseConfig")]
    internal static extern uint ChooseConfig(nint display, int* attribs, nint* configs, int configSize, int* numConfig);

    [DllImport(Lib, EntryPoint = "eglCreateContext")]
    internal static extern nint CreateContext(nint display, nint config, nint shareContext, int* attribs);

    [DllImport(Lib, EntryPoint = "eglDestroyContext")]
    internal static extern uint DestroyContext(nint display, nint context);

    [DllImport(Lib, EntryPoint = "eglMakeCurrent")]
    internal static extern uint MakeCurrent(nint display, nint draw, nint read, nint context);

    [DllImport(Lib, EntryPoint = "eglReleaseThread")]
    internal static extern uint ReleaseThread();

    [DllImport(Lib, EntryPoint = "eglGetProcAddress")]
    internal static extern nint GetProcAddress([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint AddPlatformDisplayExt(uint platform, nint nativeDisplay, int* attribs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint CreateImageKhr(nint display, nint context, uint target, nint buffer, int* attribs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate uint DestroyImageKhr(nint display, nint image);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ImageTargetTexture2DOes(uint target, nint image);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint GetStringFn(uint name);

    internal static string Describe(uint error) => error switch
    {
        0x3000 => "EGL_SUCCESS",
        0x3001 => "EGL_NOT_INITIALIZED",
        0x3002 => "EGL_BAD_ACCESS",
        0x3003 => "EGL_BAD_ALLOC",
        0x3004 => "EGL_BAD_ATTRIBUTE",
        0x3005 => "EGL_BAD_CONFIG",
        0x3006 => "EGL_BAD_CONTEXT",
        0x3007 => "EGL_BAD_CURRENT_SURFACE",
        0x3008 => "EGL_BAD_DISPLAY",
        0x3009 => "EGL_BAD_MATCH",
        0x300A => "EGL_BAD_NATIVE_PIXMAP",
        0x300B => "EGL_BAD_NATIVE_WINDOW",
        0x300C => "EGL_BAD_PARAMETER",
        0x300D => "EGL_BAD_SURFACE",
        0x300E => "EGL_CONTEXT_LOST",
        _ => "0x" + error.ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
    };
}