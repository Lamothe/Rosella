using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Rosella.GpuProbe;

/// <summary>
/// Measures what a GPU path for Rosella would actually cost on this machine:
/// API availability, a realistic full-output composite rendered by the GPU,
/// the copy into a simpledrm dumb buffer, and whether an i915 dma-buf can
/// become a framebuffer on the simpledrm card node.
/// <para>
/// Constraints honoured by construction: no DRM master, no page flip, no
/// modeset, no module or kernel-parameter change. The live compositor is
/// untouched; card nodes are opened read-write only for dumb buffers and
/// AddFB2.
/// </para>
/// </summary>
internal static unsafe class Program
{
    private const int Width = 2880;
    private const int Height = 1800;
    private const double SoftwareBaselineMs = 11.0;

    /// <summary>How long to keep the GPU busy before timing, so RPS has ramped.</summary>
    private const int ClockWarmupMs = 2000;

    /// <summary>i915's RPS frequency file for the card actually under test, if any.</summary>
    private static string? ClockPath { get; set; }

    private static void TryRun(TextWriter output, string label, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"  ({label} unavailable: {exception.Message})");
        }
    }
    private const string DefaultRenderNode = "/dev/dri/renderD128";
    private const string DefaultCard0 = "/dev/dri/card0";
    private const string DefaultCard1 = "/dev/dri/card1";

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.Help)
        {
            Options.PrintUsage();
            return 0;
        }

        var output = Console.Out;
        ClockPath = $"/sys/class/drm/{Path.GetFileName(options.Card1)}/gt_cur_freq_mhz";
        output.WriteLine("Rosella GPU probe");
        output.WriteLine($"  when       : {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
        output.WriteLine($"  kernel     : {ReadFile("/proc/sys/kernel/osrelease")?.Trim() ?? "(unknown)"}");
        output.WriteLine($"  loadavg    : {ReadFile("/proc/loadavg")?.Trim() ?? "(unknown)"}");
        output.WriteLine($"  runtime    : .NET {Environment.Version}");
        output.WriteLine();

        TryRun(output, "devices", () => PrintDevices(output));

        if (options.VulkanOnly)
        {
            TryRun(output, "vulkan", () => VulkanProbe.Run(output));
            return 0;
        }

        try
        {
            using var context = GpuContext.Create(options.RenderNode);
            PrintCapabilities(output, context);

            TryRun(output, "vulkan", () => VulkanProbe.Run(output));
            output.WriteLine();

            RunTimings(output, context, options);
        }
        catch (ProbeException exception)
        {
            output.WriteLine();
            output.WriteLine($"PROBE STOPPED: {exception.Message}");
            return 2;
        }
        catch (DllNotFoundException exception)
        {
            output.WriteLine();
            output.WriteLine($"PROBE STOPPED: a required library is missing: {exception.Message}");
            return 2;
        }

        return 0;
    }

    private static void PrintCapabilities(TextWriter output, GpuContext context)
    {
        output.WriteLine("== EGL / GBM ==");
        var renderName = Path.GetFileName(context.RenderNode);
        var renderDriver = ReadKey($"/sys/class/drm/{renderName}/device/uevent", "DRIVER=") ?? "(unknown)";
        output.WriteLine($"  render node   : {context.RenderNode} (fd {context.Fd}, kernel driver {renderDriver})");
        output.WriteLine($"  GBM backend   : {context.BackendName}");
        output.WriteLine($"  EGL vendor    : {context.EglVendor}");
        output.WriteLine($"  EGL version   : {context.EglVersion}");
        output.WriteLine($"  EGL APIs      : {context.EglClientApis}");
        output.WriteLine($"  EGL dma-buf   : EXT_image_dma_buf_import={GpuContext.HasExtension(context.EglExtensions, "EGL_EXT_image_dma_buf_import")}"
            + $" modifiers={GpuContext.HasExtension(context.EglExtensions, "EGL_EXT_image_dma_buf_import_modifiers")}"
            + $" surfaceless={GpuContext.HasExtension(context.EglExtensions, "EGL_KHR_surfaceless_context")}"
            + $" create_context={GpuContext.HasExtension(context.EglExtensions, "EGL_KHR_create_context")}");

        var info = context.EsInfo();
        output.WriteLine();
        output.WriteLine("== API generations (as enumerated by Mesa on this device) ==");
        output.WriteLine($"  GLES (ES2 requested) : {info.Version} | {info.Renderer}");
        output.WriteLine($"                  vendor: {info.Vendor} | GLSL: {info.ShadingLanguage}");

        var es3 = context.ProbeEs(3);
        if (es3.Context != 0)
        {
            output.WriteLine($"  GLES (ES3 requested) : {es3.Version} | {es3.Renderer} | GLSL: {es3.ShadingLanguage}");
            Egl.DestroyContext(context.Display, es3.Context);
        }
        else
        {
            output.WriteLine($"  GLES 3.x      : unavailable ({es3.ShadingLanguage})");
        }

        var gl = context.ProbeDesktopGl();
        if (gl.Context != 0)
        {
            output.WriteLine($"  Desktop GL    : {gl.Version} | {gl.Renderer} | GLSL: {gl.ShadingLanguage}");
            Egl.DestroyContext(context.Display, gl.Context);
        }
        else
        {
            output.WriteLine($"  Desktop GL    : unavailable ({gl.ShadingLanguage})");
        }

        // Leave ES2 current for the measurements.
        var rebound = Egl.BindApi(Egl.OpenGlEsApi);
        var current = Egl.MakeCurrent(context.Display, 0, 0, context.Context);
        output.WriteLine($"  context       : rebind ES api={rebound}, makeCurrent={current}, eglError={Egl.Describe(Egl.GetError())}");

        var maxTexture = 0;
        var viewport = stackalloc int[2];
        Gles.GetIntegerv(Gles.MaxTextureSize, &maxTexture);
        Gles.GetIntegerv(Gles.MaxViewportDims, viewport);
        output.WriteLine($"  limits        : GL_MAX_TEXTURE_SIZE={maxTexture}, GL_MAX_VIEWPORT_DIMS={viewport[0]}x{viewport[1]}");

        var glExtensions = GpuContext.Str(Gles.GetString(Gles.Extensions));
        output.WriteLine($"  readback fmt  : GL_EXT_read_format_bgra={GpuContext.HasExtension(glExtensions, "GL_EXT_read_format_bgra")}"
            + $" GL_EXT_texture_format_BGRA8888={GpuContext.HasExtension(glExtensions, "GL_EXT_texture_format_BGRA8888")}");
        output.WriteLine();
    }

    private static void RunTimings(TextWriter output, GpuContext context, Options options)
    {
        var card0 = Libc.Open(options.Card0, Libc.O_RdWr | Libc.OCloExec);
        var card0Error = card0 < 0 ? Marshal.GetLastPInvokeError() : 0;
        var card1 = Libc.Open(options.Card1, Libc.O_RdWr | Libc.OCloExec);
        var card1Error = card1 < 0 ? Marshal.GetLastPInvokeError() : 0;
        if (card0 < 0)
        {
            output.WriteLine($"cannot open {options.Card0}: {Libc.ErrorName(card0Error)} ({card0Error})");
        }

        if (card1 < 0)
        {
            output.WriteLine($"cannot open {options.Card1}: {Libc.ErrorName(card1Error)} ({card1Error})");
        }

        try
        {
            if (card0 < 0)
            {
                output.WriteLine("PROBE: no card0, so the present-path measurements and the AddFB2 test are skipped");
                return;
            }

            using var dumb = DumbBuffer.Create(card0, Width, Height);
            output.WriteLine("== Scanout-shaped dumb buffer (simpledrm card0) ==");
            output.WriteLine($"  {Width}x{Height} | pitch {dumb.Pitch} bytes | {dumb.Size} bytes ({dumb.Size / 1024.0 / 1024.0:F1} MiB) | GEM handle {dumb.Handle}");
            output.WriteLine($"  frame bytes {SceneRenderer.FrameBytes(Width, Height)}");
            if (dumb.Pitch != Width * 4)
            {
                output.WriteLine($"  note: pitch is padded by {dumb.Pitch - (Width * 4)} bytes per row");
            }

            // Defensive rebind: in one earlier ordering, work done after the
            // capability probe left no ES context current. The cause was not
            // isolated (the capability probe itself ends with
            // eglMakeCurrent(EGL_NO_CONTEXT)), so bind the context this
            // process is about to use rather than assume it survived.
            Egl.BindApi(Egl.OpenGlEsApi);
            var rebound = Egl.MakeCurrent(context.Display, 0, 0, context.Context);
            output.WriteLine($"  GL context   : makeCurrent={rebound}, {GpuContext.Str(Gles.GetString(Gles.VersionString))}, eglError={Egl.Describe(Egl.GetError())}");
            output.WriteLine();
            using var scene = new SceneRenderer(context, Width, Height);
            var target = scene.CreateColourTexture(Width, Height);
            scene.Attach(target);

            var linear = (nint)NativeMemory.AlignedAlloc((nuint)SceneRenderer.FrameBytes(Width, Height), 64);
            var linear2 = (nint)NativeMemory.AlignedAlloc((nuint)SceneRenderer.FrameBytes(Width, Height), 64);
            try
            {
                var timing = new TimingTable(output);
                var warmup = options.Warmup;
                var frames = options.Frames;

                // 1. The GPU composite on its own.
                timing.Row("gpu composite only", Measure(scene, null, null, warmup, frames, out var clock1), clock1);

                // 2. GPU composite read straight into the dumb buffer.
                timing.Row("gpu -> glReadPixels -> dumb", Measure(
                    scene,
                    () => Gles.ReadPixels(0, 0, Width, Height, Gles.Bgra, Gles.UnsignedByte, dumb.Pixels),
                    null,
                    warmup,
                    frames,
                    out var clock2), clock2);

                // 2b. The same readback through a pixel-pack buffer: the
                //     driver can use its blit engine and the CPU waits on the
                //     map instead of inside glReadPixels.
                var pbo = MakePixelPackBuffer();
                nint pboMap = 0;
                try
                {
                    timing.Row("gpu -> glReadPixels(PBO) -> memcpy -> dumb", Measure(
                        scene,
                        () =>
                        {
                            Gles.BindBuffer(Gles.PixelPackBuffer, pbo);
                            Gles.ReadPixels(0, 0, Width, Height, Gles.Bgra, Gles.UnsignedByte, 0);
                            pboMap = Gles.MapBufferRange(Gles.PixelPackBuffer, 0, (nint)SceneRenderer.FrameBytes(Width, Height), Gles.MapReadBit);
                            if (pboMap == 0)
                            {
                                throw new ProbeException($"glMapBufferRange failed (glGetError 0x{Gles.GetError():X4})");
                            }
                        },
                        () =>
                        {
                            NativeMemory.Copy((void*)pboMap, (void*)dumb.Pixels, (nuint)SceneRenderer.FrameBytes(Width, Height));
                            Gles.UnmapBuffer(Gles.PixelPackBuffer);
                            Gles.BindBuffer(Gles.PixelPackBuffer, 0);
                        },
                        warmup,
                        frames,
                        out var clockPbo), clockPbo);
                }
                finally
                {
                    Gles.BindBuffer(Gles.PixelPackBuffer, 0);
                    uint name = pbo;
                    Gles.DeleteBuffers(1, &name);
                }

                // 3. GPU composite read into a linear buffer, then memcpy
                //    into the dumb buffer: the two-step present path.
                timing.Row("gpu -> readback -> linear -> memcpy -> dumb", Measure(
                    scene,
                    () => Gles.ReadPixels(0, 0, Width, Height, Gles.Bgra, Gles.UnsignedByte, linear),
                    () => CopyRows(linear, Width * 4, dumb.Pixels, (int)dumb.Pitch, Width, Height),
                    warmup,
                    frames,
                    out var clock3), clock3);

                // Sanity: the readback buffer is not blank, and the memcpy
                // into the dumb buffer landed byte for byte.
                PrintRenderSanity(output, scene);
                var distinct = DistinctColours(linear, Width * 4, Width, Height);
                output.WriteLine($"  readback sanity: {distinct} distinct sampled colours (>1 means the GPU drew something)");
                output.WriteLine($"  dumb buffer matches the readback: {MatchesRows(linear, Width * 4, dumb.Pixels, (int)dumb.Pitch, Width, Height)}");

                // 5. The CPU copy alone: no GPU in the path at all.
                timing.Row("memcpy only (linear -> dumb)", MeasureCopies(
                    () => CopyRows(linear, Width * 4, dumb.Pixels, (int)dumb.Pitch, Width, Height),
                    reps: 40));
                timing.Row("memcpy only (linear -> linear)", MeasureCopies(
                    () => CopyRows(linear, Width * 4, linear2, Width * 4, Width, Height),
                    reps: 40));

                PrintDamageTable(output, linear, dumb);

                // 4. Best case: the GPU renders into a linear, renderable GBM
                //    bo via EGLImage, so the only CPU work is the memcpy into
                //    the dumb buffer. Last, because a driver that dislikes
                //    the unmap should not cost the evidence above.
                using (var linearTarget = options.SkipLinearTarget
                    ? null
                    : TryMakeLinearTarget(output, context, scene, Width, Height))
                {
                    if (linearTarget is not null)
                    {
                        scene.Attach(linearTarget.Texture);
                        timing.Row("gpu -> linear gbm bo -> memcpy -> dumb", Measure(
                            scene,
                            null,
                            () =>
                            {
                                var map = linearTarget.Map(out var mapStride);
                                CopyRows(map, checked((int)mapStride), dumb.Pixels, (int)dumb.Pitch, Width, Height);
                            },
                            warmup,
                            frames,
                            out var clock4), clock4);
                        scene.Attach(target);
                    }
                }


                if (card1 >= 0)
                {
                    output.WriteLine();
                    var rows = DmaBufProbe.Run(card0, card1, options.Card0, options.Card1, context, Width, Height);
                    PrintDmaBufTable(output, rows);
                }
                else
                {
                    output.WriteLine();
                    output.WriteLine($"== dma-buf import ==\n  skipped: cannot open {options.Card1}");
                }
            }
            finally
            {
                NativeMemory.AlignedFree((void*)linear);
                NativeMemory.AlignedFree((void*)linear2);
                SceneRenderer.DeleteTexture(target);
            }
        }
        finally
        {
            if (card0 >= 0)
            {
                Libc.Close(card0);
            }

            if (card1 >= 0)
            {
                Libc.Close(card1);
            }
        }
    }

    private static LinearRenderTarget? TryMakeLinearTarget(TextWriter output, GpuContext context, SceneRenderer scene, uint width, uint height)
    {
        output.WriteLine();
        output.WriteLine("== GPU-rendered linear buffer (EGLImage over a GBM bo) ==");
        if (context.CreateImage is null || context.ImageTargetTexture is null)
        {
            output.WriteLine("  unavailable: EGL_EXT_image_dma_buf_import is not exposed");
            return null;
        }

        var bo = Gbm.BoCreate(context.GbmDevice, width, height, Gbm.FormatXrgb8888, Gbm.BoUseLinear | Gbm.BoUseRendering);
        if (bo == 0)
        {
            output.WriteLine($"  gbm_bo_create failed (errno {Marshal.GetLastPInvokeError()})");
            return null;
        }

        var stride = Gbm.BoGetStride(bo);
        var offset = Gbm.BoGetOffset(bo, 0);
        var modifier = Gbm.BoGetModifier(bo);
        var primeFd = Gbm.BoGetFd(bo);
        output.WriteLine($"  bo: {width}x{height} XRGB8888 pitch={stride} offset={offset} modifier=0x{modifier:X16} dma-buf fd {primeFd}");
        if (primeFd < 0)
        {
            Gbm.BoDestroy(bo);
            return null;
        }

        // DRM_FORMAT_MOD_LINEAR is 0; MOD_INVALID (0x00FFFFFFFFFFFFFF)
        // means the driver did not report one. GBM_BO_USE_LINEAR was asked
        // for, so both spellings name the linear buffer we wanted.
        const ulong ModifierInvalid = 0x00FFFFFFFFFFFFFF;
        if (modifier != 0 && modifier != ModifierInvalid)
        {
            output.WriteLine($"  refusing: modifier 0x{modifier:X16} is not linear and no modifier import attributes are used");
            Gbm.BoDestroy(bo);
            return null;
        }

        int[] attribs =
        [
            Egl.Width, checked((int)width),
            Egl.Height, checked((int)height),
            Egl.LinuxDrmFourccExt, unchecked((int)Gbm.FormatXrgb8888),
            Egl.DmaBufPlane0FdExt, primeFd,
            Egl.DmaBufPlane0OffsetExt, checked((int)offset),
            Egl.DmaBufPlane0PitchExt, checked((int)stride),
            Egl.None,
        ];

        nint image;
        fixed (int* pointer = attribs)
        {
            image = context.CreateImage(context.Display, 0, Egl.LinuxDmaBufExt, 0, pointer);
        }

        if (image == 0)
        {
            output.WriteLine($"  eglCreateImageKHR(EGL_LINUX_DMA_BUF_EXT) failed: {Egl.Describe(Egl.GetError())}");
            Gbm.BoDestroy(bo);
            return null;
        }

        uint texture;
        try
        {
            texture = scene.CreateImageTexture(image);
            scene.Attach(texture);
        }
        catch (ProbeException exception)
        {
            output.WriteLine($"  EGLImage -> texture failed: {exception.Message}");
            if (context.DestroyImage is { } destroy)
            {
                destroy(context.Display, image);
            }

            Gbm.BoDestroy(bo);
            return null;
        }

        output.WriteLine($"  render target imported: {Gbm.BoGetWidth(bo)}x{Gbm.BoGetHeight(bo)}, pitch {stride}");
        return new LinearRenderTarget(context, bo, image, primeFd, texture, stride, width, height);
    }

    /// <summary>
    /// A GBM bo the GPU renders into through an EGLImage, with the CPU side
    /// mapped straight from the dma-buf (not gbm_bo_map: Mesa's gbm unmap of
    /// an imported render target is where drivers crash, and a compositor
    /// would mmap the dma-buf itself anyway).
    /// </summary>
    private sealed unsafe class LinearRenderTarget : IDisposable
    {
        private readonly GpuContext _context;
        private readonly nint _bo;
        private readonly nint _image;
        private readonly int _primeFd;
        private uint _stride;
        private readonly uint _width;
        private readonly uint _height;
        private nint _map;
        private bool _disposed;

        internal LinearRenderTarget(GpuContext context, nint bo, nint image, int primeFd, uint texture, uint stride, uint width, uint height)
        {
            _context = context;
            _bo = bo;
            _image = image;
            _primeFd = primeFd;
            Texture = texture;
            _stride = stride;
            _width = width;
            _height = height;
        }

        internal uint Texture { get; }

        internal nint Map(out uint stride)
        {
            if (_map == 0)
            {
                // mmap of the dma-buf rather than gbm_bo_map: on i915 both
                // give a write-combined mapping (so both read at ~3.4 GB/s),
                // but Mesa's gbm_bo_unmap of an imported render target
                // segfaults on both i915 and amdgpu, so the plain mapping is
                // the safer mechanism to measure with.
                var length = checked((nuint)_stride * _height);
                _map = Libc.Mmap(0, length, Libc.ProtRead, Libc.MapShared, _primeFd, 0);
                if (_map == -1)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    throw new ProbeException($"mmap(dma-buf {length} bytes) failed: {Libc.ErrorName(errno)} ({errno})");
                }
            }

            stride = _stride;
            return _map;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_map != 0)
            {
                Libc.Munmap(_map, checked((nuint)_stride * _height));
            }

            SceneRenderer.DeleteTexture(Texture);
            if (_context.DestroyImage is { } destroy)
            {
                destroy(_context.Display, _image);
            }

            Libc.Close(_primeFd);
            Gbm.BoDestroy(_bo);
        }
    }

    private static uint MakePixelPackBuffer()
    {
        uint pbo;
        Gles.GenBuffers(1, &pbo);
        Gles.BindBuffer(Gles.PixelPackBuffer, pbo);
        Gles.BufferData(Gles.PixelPackBuffer, (nint)SceneRenderer.FrameBytes(Width, Height), 0, Gles.StreamRead);
        Gles.BindBuffer(Gles.PixelPackBuffer, 0);
        if (Gles.GetError() != Gles.NoError)
        {
            throw new ProbeException("pixel-pack buffer creation failed");
        }

        return pbo;
    }

    private static FrameSample[] Measure(SceneRenderer scene, Action? readback, Action? copy, int warmup, int frames)
        => Measure(scene, readback, copy, warmup, frames, out _);

    /// <summary>
    /// Times frames, warming the GPU clock first: i915's RPS starts at RPn
    /// (200 MHz here) and needs a second or two of sustained work before it
    /// settles, and a benchmark that ignores that reports the idle clock.
    /// </summary>
    private static FrameSample[] Measure(SceneRenderer scene, Action? readback, Action? copy, int warmup, int frames, out (int Min, int Max, int Median) clockMhz)
    {
        var clocks = new List<int>();
        var clockWarmup = Stopwatch.StartNew();
        while (clockWarmup.ElapsedMilliseconds < ClockWarmupMs)
        {
            scene.Draw();
            Gles.Finish();
        }

        var samples = new FrameSample[frames];
        for (var frame = 0; frame < warmup + frames; frame++)
        {
            var clock = ReadGpuClockMhz();
            if (clock > 0)
            {
                clocks.Add(clock);
            }

            var start = Stopwatch.GetTimestamp();
            scene.Draw();
            Gles.Finish();
            var rendered = Stopwatch.GetTimestamp();
            readback?.Invoke();
            var transferred = Stopwatch.GetTimestamp();
            copy?.Invoke();
            var copied = Stopwatch.GetTimestamp();

            if (frame >= warmup)
            {
                samples[frame - warmup] = new FrameSample(
                    Milliseconds(rendered - start),
                    Milliseconds(transferred - rendered),
                    Milliseconds(copied - transferred));
            }
        }

        clockMhz = clocks.Count == 0 ? default : (clocks.Min(), clocks.Max(), clocks.Order().ElementAt(clocks.Count / 2));
        return samples;
    }

    private static FrameSample[] MeasureCopies(Action copy, int reps)
    {
        // Warm the pages up before timing.
        copy();
        var samples = new FrameSample[reps];
        for (var i = 0; i < reps; i++)
        {
            var start = Stopwatch.GetTimestamp();
            copy();
            var end = Stopwatch.GetTimestamp();
            samples[i] = new FrameSample(0, 0, Milliseconds(end - start));
        }

        return samples;
    }

    /// <summary>
    /// Prints the timing table one row at a time, so a driver that dies in a
    /// later variant still leaves every completed result on the terminal.
    /// </summary>
    private sealed class TimingTable(TextWriter output)
    {
        private const double Bytes = (double)Width * Height * 4;

        internal void Row(string name, FrameSample[] samples, (int Min, int Max, int Median) clockMhz = default)
        {
            if (!_header)
            {
                output.WriteLine();
                output.WriteLine($"== Timing: {Width}x{Height}, median over {samples.Length} frames after warm-up ==");
                output.WriteLine("  variant                                        render ms  readback ms   copy ms  total ms   fps   vs 11.0 ms");
                _header = true;
            }

            var render = Stats.From(samples.Select(s => s.RenderMs));
            var readback = Stats.From(samples.Select(s => s.TransferMs));
            var copy = Stats.From(samples.Select(s => s.CopyMs));
            var total = Stats.From(samples.Select(s => s.TotalMs));
            output.WriteLine(
                $"  {name,-44} {render.Median,9:F2} {readback.Median,11:F2} {copy.Median,9:F2} {total.Median,9:F2} {1000.0 / Math.Max(total.Median, 0.0001),6:F1}   {total.Median / SoftwareBaselineMs,6:F2}x");
            var clock = clockMhz.Max > 0
                ? $" | GPU clock median {clockMhz.Median} MHz ({clockMhz.Min}-{clockMhz.Max})"
                : " | GPU clock n/a";
            output.WriteLine(
                $"  {"",-44} p95 total {total.P95:F2} ms | min {total.Min:F2} | max {total.Max:F2} | {Bytes / 1e6 / Math.Max(total.Median, 0.0001):F1} GB/s{clock}");
        }

        private bool _header;
    }

    private static void PrintDamageTable(TextWriter output, nint source, DumbBuffer dumb)
    {
        output.WriteLine();
        output.WriteLine("== Copy cost by damage size (linear -> dumb, row by row) ==");
        output.WriteLine("  damage rect          bytes        median ms   GB/s (payload)");
        (int Width, int Height)[] rects = [(64, 64), (2400, 1000), (Width, Height)];
        foreach (var (width, height) in rects)
        {
            var samples = MeasureCopies(
                () => CopyRows(source, Width * 4, dumb.Pixels, (int)dumb.Pitch, width, height),
                reps: width * height > 1_000_000 ? 40 : 200);
            var stats = Stats.From(samples.Select(s => s.CopyMs));
            var bytes = (double)width * height * 4;
            output.WriteLine(
                $"  {width,4}x{height,-4} {bytes,12:N0} {stats.Median,12:F3} {bytes / 1e6 / Math.Max(stats.Median, 0.0001),10:F2}");
        }
    }

    private static void PrintRenderSanity(TextWriter output, SceneRenderer scene)
    {
        scene.Draw();
        Gles.Finish();
        var error = Gles.GetError();
        uint centre = 0;
        uint corner = 0;
        Gles.ReadPixels(Width / 2, Height / 2, 1, 1, Gles.Bgra, Gles.UnsignedByte, (nint)(&centre));
        Gles.ReadPixels(2, 2, 1, 1, Gles.Bgra, Gles.UnsignedByte, (nint)(&corner));
        var viewport = stackalloc int[4];
        Gles.GetIntegerv(0x0BA2, viewport);
        output.WriteLine($"  render sanity : centre=0x{centre:X8} corner=0x{corner:X8} glGetError={error} viewport={viewport[2]}x{viewport[3]}");
    }

    private static void PrintDmaBufTable(TextWriter output, List<DmaBufProbe.Row> rows)
    {
        output.WriteLine("== dma-buf import on the simpledrm card node (no flip, no master) ==");
        output.WriteLine("  target            source                              call                        result");
        foreach (var row in rows)
        {
            output.WriteLine($"  {row.Target,-17} {row.Source,-35} {row.Call,-27} {row.Result}");
        }
    }

    private static void CopyRows(nint source, int sourceStride, nint target, int targetStride, int width, int height)
    {
        var bytes = checked((nuint)(width * 4));
        for (var row = 0; row < height; row++)
        {
            NativeMemory.Copy((byte*)source + ((nint)row * sourceStride), (byte*)target + ((nint)row * targetStride), bytes);
        }
    }

    private static int DistinctColours(nint buffer, int stride, int width, int height)
    {
        var seen = new HashSet<int>();
        for (var y = 0; y < height; y += 97)
        {
            for (var x = 0; x < width; x += 89)
            {
                seen.Add(*(int*)((byte*)buffer + ((nint)y * stride) + ((nint)x * 4)));
            }
        }

        return seen.Count;
    }

    private static bool MatchesRows(nint left, int leftStride, nint right, int rightStride, int width, int height)
    {
        var bytes = width * 4;
        for (var y = 0; y < height; y++)
        {
            if (!new ReadOnlySpan<byte>((byte*)left + ((nint)y * leftStride), bytes)
                .SequenceEqual(new ReadOnlySpan<byte>((byte*)right + ((nint)y * rightStride), bytes)))
            {
                return false;
            }
        }

        return true;
    }

    private static void PrintDevices(TextWriter output)
    {
        output.WriteLine("== Devices ==");
        foreach (var node in Directory.GetFiles("/dev/dri").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(node);
            var driver = ReadKey($"/sys/class/drm/{name}/device/uevent", "DRIVER=") ?? "(no driver)";
            output.WriteLine($"  /dev/dri/{name}: driver={driver}");
            if (!name.StartsWith("card", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var connector in Directory.GetDirectories("/sys/class/drm", $"{name}-*").Order(StringComparer.Ordinal))
            {
                var status = ReadFile(Path.Combine(connector, "status"))?.Trim() ?? "?";
                var enabled = ReadFile(Path.Combine(connector, "enabled"))?.Trim() ?? "?";
                var modes = ReadFile(Path.Combine(connector, "modes"))?
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
                var first = modes.Length > 0 ? modes[0] : "(no modes)";
                output.WriteLine($"    {Path.GetFileName(connector),-28} {status,-12} enabled={enabled,-8} modes={modes.Length} first={first}");
            }
        }

        output.WriteLine("  note: this probe never takes DRM master, never page-flips and never modesets.");
        output.WriteLine();
    }

    private static string? ReadKey(string path, string prefix)
    {
        var text = ReadFile(path);
        if (text is null)
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>The i915 RPS frequency, when the kernel exposes it; 0 otherwise.</summary>
    private static int ReadGpuClockMhz()
    {
        if (ClockPath is null)
        {
            return 0;
        }

        try
        {
            var text = File.ReadAllText(ClockPath);
            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private readonly record struct FrameSample(double RenderMs, double TransferMs, double CopyMs)
    {
        internal double TotalMs => RenderMs + TransferMs + CopyMs;
    }

    private readonly record struct Stats(int Count, double Median, double Mean, double Min, double Max, double P95)
    {
        internal static Stats From(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            if (sorted.Length == 0)
            {
                return default;
            }

            var median = sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
            var p95Index = Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1);
            return new Stats(sorted.Length, median, sorted.Average(), sorted[0], sorted[^1], sorted[p95Index]);
        }
    }

    private sealed record Options
    {
        internal string RenderNode { get; init; } = DefaultRenderNode;

        internal string Card0 { get; init; } = DefaultCard0;

        internal string Card1 { get; init; } = DefaultCard1;

        internal int Warmup { get; init; } = 5;

        internal int Frames { get; init; } = 60;

        internal bool VulkanOnly { get; init; }

        internal bool SkipLinearTarget { get; init; }

        internal bool Help { get; init; }

        internal static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--render-node" when i + 1 < args.Length:
                        options = options with { RenderNode = args[++i] };
                        break;
                    case "--card0" when i + 1 < args.Length:
                        options = options with { Card0 = args[++i] };
                        break;
                    case "--card1" when i + 1 < args.Length:
                        options = options with { Card1 = args[++i] };
                        break;
                    case "--frames" when i + 1 < args.Length:
                        options = options with { Frames = int.Parse(args[++i], CultureInfo.InvariantCulture) };
                        break;
                    case "--warmup" when i + 1 < args.Length:
                        options = options with { Warmup = int.Parse(args[++i], CultureInfo.InvariantCulture) };
                        break;
                    case "--vulkan-only":
                        options = options with { VulkanOnly = true };
                        break;
                    case "--skip-linear-target":
                        options = options with { SkipLinearTarget = true };
                        break;
                    case "--help" or "-h":
                        options = options with { Help = true };
                        break;
                    default:
                        Console.Error.WriteLine($"unrecognised argument: {args[i]}");
                        break;
                }
            }

            return options;
        }

        internal static void PrintUsage()
        {
            Console.WriteLine("usage: Rosella.GpuProbe [--render-node PATH] [--card0 PATH] [--card1 PATH]");
            Console.WriteLine("                        [--frames N] [--warmup N] [--vulkan-only] [--skip-linear-target] [--help]");
            Console.WriteLine();
            Console.WriteLine("  Defaults: /dev/dri/renderD128, /dev/dri/card0, /dev/dri/card1, 60 frames, 5 warm-up.");
            Console.WriteLine("  Nothing here takes DRM master or page-flips; the live compositor is untouched.");
            Console.WriteLine("  --vulkan-only reports the Vulkan ICDs the loader enumerates and exits.");
        }
    }
}