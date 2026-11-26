using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rosella.Compositor;

internal readonly record struct DmaBufFormat(uint Fourcc, ulong Modifier);
internal readonly record struct DmaBufPlane(int Fd, uint Offset, uint Stride, ulong Modifier);

/// <summary>Imports GPU client buffers into an EGL context and snapshots them for the CPU scene.</summary>
internal sealed unsafe class DmaBufReader : IDisposable
{
    internal const uint Xrgb8888 = 0x34325258;
    internal const uint Argb8888 = 0x34325241;
    internal const ulong InvalidModifier = 0x00FFFFFFFFFFFFFF;
    private const int EglNone = 0x3038;
    private const uint Texture2D = 0x0DE1;
    private const uint Framebuffer = 0x8D40;
    private const uint ColorAttachment = 0x8CE0;
    private const uint Rgba = 0x1908;
    private const uint Bgra = 0x80E1;
    private const uint UnsignedByte = 0x1401;

    private readonly object _gate = new();
    private readonly List<DmaBufFormat> _formats = [];
    private SafeFileHandle? _deviceFd;
    private nint _gbm;
    private nint _display;
    private nint _context;
    private bool _initialized;
    private bool _disposed;
    private bool _reportedReadback;
    private int _reportedReadError;
    private CreateImage _createImage = null!;
    private DestroyImage _destroyImage = null!;
    private BindImage _bindImage = null!;

    public IReadOnlyList<DmaBufFormat> Formats => _formats;

    public byte[] DeviceId { get; private set; } = [];

    internal SafeFileHandle? TimelineDevice { get; private set; }

    public static string? FindRenderNode(string cardPath)
    {
        var card = Directory.ResolveLinkTarget($"/sys/class/drm/{Path.GetFileName(cardPath)}/device", true);
        if (card is null)
        {
            return null;
        }

        foreach (var path in Directory.EnumerateDirectories("/sys/class/drm", "renderD*"))
        {
            if (Directory.ResolveLinkTarget(Path.Combine(path, "device"), true)?.FullName == card.FullName)
            {
                return Path.Combine("/dev/dri", Path.GetFileName(path));
            }
        }

        return null;
    }

    public static DmaBufReader? TryOpen(string? renderNode)
    {
        if (renderNode is null)
        {
            Console.Error.WriteLine("rosella: DMA-BUF unavailable, no matching DRM render node");
            return null;
        }

        var reader = new DmaBufReader();
        try
        {
            reader.Initialize(renderNode);
            Console.Error.WriteLine($"rosella: DMA-BUF enabled on {renderNode}, {reader.Formats.Count} format/modifier pairs");
            return reader;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            reader.Dispose();
            Console.Error.WriteLine($"rosella: DMA-BUF unavailable on {renderNode}, {error.Message}");
            return null;
        }
    }

    private void Initialize(string renderNode)
    {
        // Linux dev_t layout. DRM major/minor numbers fit in the low fields.
        var device = File.ReadAllText($"/sys/class/drm/{Path.GetFileName(renderNode)}/dev").Trim().Split(':');
        var major = uint.Parse(device[0], System.Globalization.CultureInfo.InvariantCulture);
        var minor = uint.Parse(device[1], System.Globalization.CultureInfo.InvariantCulture);
        var deviceId = ((ulong)major << 8) | (minor & 0xffu) | ((ulong)(minor & ~0xffu) << 12);
        DeviceId = BitConverter.GetBytes(deviceId);

        _deviceFd = File.OpenHandle(renderNode, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (DrmSyncobjNative.SupportsTimelines(_deviceFd))
        {
            TimelineDevice = _deviceFd;
        }
        _gbm = gbm_create_device((int)_deviceFd.DangerousGetHandle());
        if (_gbm == 0)
        {
            throw new InvalidOperationException("GBM device creation failed");
        }

        const uint platformGbm = 0x31D7;
        _display = eglGetPlatformDisplay(platformGbm, _gbm, 0);
        if (_display == 0 || eglInitialize(_display, out _, out _) == 0)
        {
            throw EglError("EGL initialization");
        }

        _initialized = true;
        var extensions = Marshal.PtrToStringUTF8(eglQueryString(_display, 0x3055))?.Split(' ') ?? [];
        if (!extensions.Contains("EGL_KHR_surfaceless_context") ||
            !extensions.Contains("EGL_EXT_image_dma_buf_import_modifiers"))
        {
            throw new InvalidOperationException("required EGL DMA-BUF or surfaceless support is missing");
        }

        _createImage = Load<CreateImage>("eglCreateImageKHR");
        _destroyImage = Load<DestroyImage>("eglDestroyImageKHR");
        _bindImage = Load<BindImage>("glEGLImageTargetTexture2DOES");
        if (eglBindAPI(0x30A0) == 0)
        {
            throw EglError("OpenGL ES API binding");
        }

        int* configAttributes = stackalloc int[] { 0x3033, 4, 0x3040, 4, 0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, EglNone };
        if (eglChooseConfig(_display, configAttributes, out var config, 1, out var count) == 0 || count == 0)
        {
            throw EglError("OpenGL ES config selection");
        }

        int* contextAttributes = stackalloc int[] { 0x3098, 2, EglNone };
        _context = eglCreateContext(_display, config, 0, contextAttributes);
        if (_context == 0)
        {
            throw EglError("OpenGL ES context creation");
        }

        var queryModifiers = Load<QueryModifiers>("eglQueryDmaBufModifiersEXT");
        ProbeFormats(Xrgb8888, queryModifiers);
        ProbeFormats(Argb8888, queryModifiers);
        if (_formats.Count == 0)
        {
            throw new InvalidOperationException("no renderable DMA-BUF formats passed the readback check");
        }
    }

    private void ProbeFormats(uint fourcc, QueryModifiers query)
    {
        ProbeFormat(fourcc, size: 64);
        if (query(_display, (int)fourcc, 0, null, null, out var count) == 0 || count <= 0)
        {
            return;
        }

        var modifiers = new ulong[count];
        var externalOnly = new uint[count];
        fixed (ulong* layouts = modifiers)
        fixed (uint* external = externalOnly)
        {
            if (query(_display, (int)fourcc, count, layouts, external, out count) == 0)
            {
                return;
            }
        }
        for (var index = 0; index < count; index++)
        {
            var modifier = modifiers[index];
            if (externalOnly[index] == 0 && !_formats.Contains(new(fourcc, modifier)))
            {
                // Small allocations may fall back to a shorter tile layout.
                // Validate the renderable layouts apps can use for real windows.
                ProbeFormat(fourcc, size: 640, modifier);
            }
        }
    }

    private void ProbeFormat(uint fourcc, int size, ulong? requested = null)
    {
        const uint useRendering = 1 << 2;
        var modifier = requested.GetValueOrDefault();
        var bo = requested.HasValue
            ? gbm_bo_create_with_modifiers2(_gbm, (uint)size, (uint)size, fourcc, &modifier, 1, useRendering)
            : gbm_bo_create(_gbm, (uint)size, (uint)size, fourcc, useRendering);
        if (bo == 0)
        {
            if (!requested.HasValue)
            {
                Console.Error.WriteLine($"rosella: DMA-BUF format 0x{fourcc:X8} could not allocate a GBM buffer");
            }
            return;
        }

        try
        {
            if (gbm_bo_get_plane_count(bo) != 1 || gbm_bo_get_fd(bo) is not (>= 0 and var fd))
            {
                Console.Error.WriteLine($"rosella: DMA-BUF format 0x{fourcc:X8} could not export one plane");
                return;
            }

            using var handle = new SafeFileHandle(fd, ownsHandle: true);
            var plane = new DmaBufPlane(fd, gbm_bo_get_offset(bo, 0), gbm_bo_get_stride(bo), gbm_bo_get_modifier(bo));
            var format = new DmaBufFormat(fourcc, plane.Modifier);
            if (_formats.Contains(format))
            {
                return;
            }
            var image = Import(size, size, fourcc, plane);
            if (image == 0)
            {
                Console.Error.WriteLine($"rosella: DMA-BUF format 0x{fourcc:X8}, modifier 0x{plane.Modifier:X16}: {EglError("EGL import").Message}");
                return;
            }

            try
            {
                if (Read(image, size, size, false, null, probe: true) is { } pixels &&
                    pixels[0] is 191 or 192 && pixels[1] is 127 or 128 && pixels[2] is 63 or 64)
                {
                    _formats.Add(format);
                }
            }
            finally
            {
                Release(image);
            }
        }
        finally
        {
            gbm_bo_destroy(bo);
        }
    }

    public nint Import(int width, int height, uint fourcc, DmaBufPlane plane)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            int* attributes = stackalloc int[]
            {
                0x3057, width, 0x3056, height, 0x3271, (int)fourcc,
                0x3272, plane.Fd, 0x3273, (int)plane.Offset, 0x3274, (int)plane.Stride,
                0x3443, (int)plane.Modifier, 0x3444, (int)(plane.Modifier >> 32), EglNone,
            };
            if (plane.Modifier == InvalidModifier)
            {
                attributes[12] = EglNone;
            }

            return _createImage(_display, 0, 0x3270, 0, attributes);
        }
    }

    public byte[]? Read(nint image, int width, int height, bool invertY, byte[]? reusable, bool probe = false)
    {
        lock (_gate)
        {
            if (_disposed || eglMakeCurrent(_display, 0, 0, _context) == 0)
            {
                if (probe)
                {
                    Console.Error.WriteLine($"rosella: {EglError("DMA-BUF readback context binding").Message}");
                }
                return null;
            }

            uint texture = 0;
            uint framebuffer = 0;
            try
            {
                glGenTextures(1, &texture);
                glBindTexture(Texture2D, texture);
                _bindImage(Texture2D, image);
                glGenFramebuffers(1, &framebuffer);
                glBindFramebuffer(Framebuffer, framebuffer);
                glFramebufferTexture2D(Framebuffer, ColorAttachment, Texture2D, texture, 0);
                var error = glGetError();
                var framebufferStatus = glCheckFramebufferStatus(Framebuffer);
                if (error != 0 || framebufferStatus != 0x8CD5)
                {
                    if (probe)
                    {
                        Console.Error.WriteLine($"rosella: DMA-BUF framebuffer status 0x{framebufferStatus:X}, GL error 0x{error:X}");
                    }
                    return null;
                }

                if (probe)
                {
                    glClearColor(0.25f, 0.5f, 0.75f, 1);
                    glClear(0x4000);
                }

                glGetIntegerv(0x8B9B, out var preferredFormat);
                glGetIntegerv(0x8B9A, out var preferredType);
                var format = preferredFormat == Bgra && preferredType == UnsignedByte ? Bgra : Rgba;
                var length = checked(width * height * 4);
                var pixels = reusable?.Length == length ? reusable : GC.AllocateUninitializedArray<byte>(length);
                fixed (byte* data = pixels)
                {
                    glReadPixels(0, 0, width, height, format, UnsignedByte, data);
                }

                error = glGetError();
                if (error != 0)
                {
                    if (probe)
                    {
                        Console.Error.WriteLine($"rosella: DMA-BUF glReadPixels error 0x{error:X}");
                    }
                    return null;
                }

                if (format == Rgba)
                {
                    var packedPixels = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
                    foreach (ref var pixel in packedPixels)
                    {
                        pixel = (pixel & 0xFF00FF00) | ((pixel & 0xFF) << 16) | ((pixel >> 16) & 0xFF);
                    }
                }

                if (invertY)
                {
                    var stride = width * 4;
                    var scratch = new byte[stride];
                    for (var y = 0; y < height / 2; y++)
                    {
                        var top = pixels.AsSpan(y * stride, stride);
                        var bottom = pixels.AsSpan((height - y - 1) * stride, stride);
                        top.CopyTo(scratch);
                        bottom.CopyTo(top);
                        scratch.CopyTo(bottom);
                    }
                }

                if (!probe && !_reportedReadback)
                {
                    _reportedReadback = true;
                    Console.Error.WriteLine($"rosella: first DMA-BUF frame read back ({width}x{height})");
                }

                return pixels;
            }
            finally
            {
                glDeleteFramebuffers(1, &framebuffer);
                glDeleteTextures(1, &texture);
                eglMakeCurrent(_display, 0, 0, 0);
            }
        }
    }

    public void ReportReadError(int width, int height)
    {
        if (Interlocked.Exchange(ref _reportedReadError, 1) == 0)
        {
            Console.Error.WriteLine($"rosella: could not read DMA-BUF buffer {width}x{height}");
        }
    }

    public void Release(nint image)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _destroyImage(_display, image);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_context != 0)
            {
                eglDestroyContext(_display, _context);
            }

            if (_initialized)
            {
                eglTerminate(_display);
            }

            if (_gbm != 0)
            {
                gbm_device_destroy(_gbm);
            }

            _deviceFd?.Dispose();
        }
    }

    private static InvalidOperationException EglError(string operation) =>
        new($"{operation} failed with EGL error 0x{eglGetError():X}");

    private static T Load<T>(string name) where T : Delegate
    {
        var address = eglGetProcAddress(name);
        return address == 0
            ? throw new InvalidOperationException($"{name} is unavailable")
            : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateImage(nint display, nint context, uint target, nint buffer, int* attributes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint DestroyImage(nint display, nint image);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BindImage(uint target, nint image);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint QueryModifiers(nint display, int fourcc, int capacity, ulong* modifiers, uint* externalOnly, out int count);

    private const string Gbm = "libgbm.so.1";
    private const string Egl = "libEGL.so.1";
    private const string Gles = "libGLESv2.so.2";

    [DllImport(Gbm)] private static extern nint gbm_create_device(int fd);
    [DllImport(Gbm)] private static extern void gbm_device_destroy(nint device);
    [DllImport(Gbm)] private static extern nint gbm_bo_create(nint device, uint width, uint height, uint format, uint flags);
    [DllImport(Gbm)] private static extern nint gbm_bo_create_with_modifiers2(nint device, uint width, uint height, uint format,
        ulong* modifiers, uint count, uint flags);
    [DllImport(Gbm)] private static extern void gbm_bo_destroy(nint bo);
    [DllImport(Gbm)] private static extern int gbm_bo_get_plane_count(nint bo);
    [DllImport(Gbm)] private static extern int gbm_bo_get_fd(nint bo);
    [DllImport(Gbm)] private static extern uint gbm_bo_get_offset(nint bo, int plane);
    [DllImport(Gbm)] private static extern uint gbm_bo_get_stride(nint bo);
    [DllImport(Gbm)] private static extern ulong gbm_bo_get_modifier(nint bo);

    [DllImport(Egl)] private static extern nint eglGetPlatformDisplay(uint platform, nint nativeDisplay, nint attributes);
    [DllImport(Egl)] private static extern uint eglInitialize(nint display, out int major, out int minor);
    [DllImport(Egl)] private static extern uint eglTerminate(nint display);
    [DllImport(Egl)] private static extern nint eglQueryString(nint display, int name);
    [DllImport(Egl)] private static extern uint eglBindAPI(uint api);
    [DllImport(Egl)] private static extern uint eglChooseConfig(nint display, int* attributes, out nint config, int configSize, out int count);
    [DllImport(Egl)] private static extern nint eglCreateContext(nint display, nint config, nint share, int* attributes);
    [DllImport(Egl)] private static extern uint eglDestroyContext(nint display, nint context);
    [DllImport(Egl)] private static extern uint eglMakeCurrent(nint display, nint draw, nint read, nint context);
    [DllImport(Egl)] private static extern uint eglGetError();
    [DllImport(Egl)] private static extern nint eglGetProcAddress([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Gles)] private static extern void glGenTextures(int count, uint* textures);
    [DllImport(Gles)] private static extern void glDeleteTextures(int count, uint* textures);
    [DllImport(Gles)] private static extern void glBindTexture(uint target, uint texture);
    [DllImport(Gles)] private static extern void glGenFramebuffers(int count, uint* framebuffers);
    [DllImport(Gles)] private static extern void glDeleteFramebuffers(int count, uint* framebuffers);
    [DllImport(Gles)] private static extern void glBindFramebuffer(uint target, uint framebuffer);
    [DllImport(Gles)] private static extern void glFramebufferTexture2D(uint target, uint attachment, uint textureTarget, uint texture, int level);
    [DllImport(Gles)] private static extern uint glCheckFramebufferStatus(uint target);
    [DllImport(Gles)] private static extern void glGetIntegerv(uint name, out int value);
    [DllImport(Gles)] private static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, byte* data);
    [DllImport(Gles)] private static extern void glClearColor(float red, float green, float blue, float alpha);
    [DllImport(Gles)] private static extern void glClear(uint mask);
    [DllImport(Gles)] private static extern uint glGetError();
}
