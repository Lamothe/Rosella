namespace Rosella.GpuProbe;

/// <summary>
/// The composite the probe measures: a full-output wallpaper, one opaque
/// window with a title bar, and a cursor — four draw calls, about 8.8 M
/// fragments at 2880×1800 (the window overdraws the wallpaper, as it would in
/// a compositor that does not scissor opaque windows out of the background).
/// </summary>
internal sealed unsafe class SceneRenderer : IDisposable
{
    private const uint Triangles = 0x0004;
    private const int QuadCount = 4;
    private const int FloatsPerVertex = 8;
    private const int VerticesPerQuad = 6;

    private readonly uint _program;
    private readonly uint _vbo;
    private readonly uint _wallpaper;
    private readonly uint _white;
    private readonly uint _cursor;
    private readonly uint _framebuffer;
    private readonly int _width;
    private readonly int _height;
    private readonly int _uniformTexture;
    private readonly int _uniformSize;
    private uint _attached;

    private readonly Egl.ImageTargetTexture2DOes? _imageTargetTexture;

    private bool _disposed;

    internal SceneRenderer(GpuContext context, int width, int height)
    {
        _width = width;
        _height = height;
        _imageTargetTexture = context.ImageTargetTexture;

        _program = BuildProgram();
        _uniformTexture = Gles.GetUniformLocation(_program, "uTexture");
        _uniformSize = Gles.GetUniformLocation(_program, "uSize");
        if (_uniformTexture < 0 || _uniformSize < 0)
        {
            throw new ProbeException("shader uniform was optimised out: uTexture/uSize");
        }

        _wallpaper = MakeWallpaperTexture();
        _white = MakeSolidTexture(255, 255, 255, 255);
        _cursor = MakeCursorTexture();
        _vbo = MakeGeometry();

        uint framebuffer;
        Gles.GenFramebuffers(1, &framebuffer);
        _framebuffer = framebuffer;
    }

    /// <summary>Number of bytes one full frame occupies: the unit the copies move.</summary>
    internal static long FrameBytes(int width, int height) => (long)width * height * 4;

    /// <summary>Renders the scene into the currently attached colour target.</summary>
    internal void Draw()
    {
        Gles.BindFramebuffer(Gles.Framebuffer, _framebuffer);
        Gles.Viewport(0, 0, _width, _height);
        Gles.ClearColor(0.05f, 0.06f, 0.09f, 1f);
        Gles.Clear(Gles.ColorBufferBit);
        Gles.UseProgram(_program);
        Gles.Uniform2f(_uniformSize, _width, _height);
        Gles.BindBuffer(Gles.ArrayBuffer, _vbo);

        const int stride = FloatsPerVertex * sizeof(float);
        Gles.VertexAttribPointer(0, 2, Gles.Float, 0, stride, 0);
        Gles.VertexAttribPointer(1, 2, Gles.Float, 0, stride, 2 * sizeof(float));
        Gles.VertexAttribPointer(2, 4, Gles.Float, 0, stride, 4 * sizeof(float));
        Gles.EnableVertexAttribArray(0);
        Gles.EnableVertexAttribArray(1);
        Gles.EnableVertexAttribArray(2);
        Gles.Uniform1i(_uniformTexture, 0);

        // Wallpaper, then the window body and title bar (opaque), then the
        // cursor blended on top.
        Gles.Disable(Gles.Blend);
        Gles.BindTexture(Gles.Texture2D, _wallpaper);
        Gles.DrawArrays(Triangles, 0, VerticesPerQuad);
        Gles.BindTexture(Gles.Texture2D, _white);
        Gles.DrawArrays(Triangles, VerticesPerQuad, VerticesPerQuad);
        Gles.DrawArrays(Triangles, 2 * VerticesPerQuad, VerticesPerQuad);
        Gles.Enable(Gles.Blend);
        Gles.BlendFunc(Gles.SrcAlpha, Gles.OneMinusSrcAlpha);
        Gles.BindTexture(Gles.Texture2D, _cursor);
        Gles.DrawArrays(Triangles, 3 * VerticesPerQuad, VerticesPerQuad);
        Gles.Disable(Gles.Blend);
    }

    /// <summary>Attaches an RGBA8 texture as the colour target. The caller owns the texture.</summary>
    internal void Attach(uint texture)
    {
        _attached = texture;
        Gles.BindFramebuffer(Gles.Framebuffer, _framebuffer);
        Gles.FramebufferTexture2D(Gles.Framebuffer, Gles.ColorAttachment0, Gles.Texture2D, texture, 0);
        var status = Gles.CheckFramebufferStatus(Gles.Framebuffer);
        if (status != Gles.FramebufferComplete)
        {
            throw new ProbeException($"framebuffer incomplete: 0x{status:X4}");
        }

        Gles.BindFramebuffer(Gles.Framebuffer, 0);
    }

    /// <summary>The texture name currently attached (0 once deleted).</summary>
    internal uint Attached => _attached;

    internal static void DeleteTexture(uint texture)
    {
        if (texture == 0)
        {
            return;
        }

        uint name = texture;
        Gles.DeleteTextures(1, &name);
    }

    internal uint CreateColourTexture(int width, int height)
    {
        uint texture;
        Gles.GenTextures(1, &texture);
        Gles.BindTexture(Gles.Texture2D, texture);
        Gles.TexImage2D(Gles.Texture2D, 0, unchecked((int)Gles.Rgba8), width, height, 0, Gles.Rgba, Gles.UnsignedByte, 0);
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureMinFilter, unchecked((int)Gles.Nearest));
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureMagFilter, unchecked((int)Gles.Nearest));
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureWrapS, unchecked((int)Gles.ClampToEdge));
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureWrapT, unchecked((int)Gles.ClampToEdge));
        return texture;
    }

    internal uint CreateImageTexture(nint image)
    {
        var target = _imageTargetTexture ?? throw new ProbeException("glEGLImageTargetTexture2DOES is unavailable");
        uint texture;
        Gles.GenTextures(1, &texture);
        Gles.BindTexture(Gles.Texture2D, texture);
        target(Gles.Texture2D, image);
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureMinFilter, unchecked((int)Gles.Nearest));
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureMagFilter, unchecked((int)Gles.Nearest));
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureWrapS, unchecked((int)Gles.ClampToEdge));
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureWrapT, unchecked((int)Gles.ClampToEdge));
        if (Gles.GetError() != Gles.NoError)
        {
            throw new ProbeException("glEGLImageTargetTexture2DOES reported a GL error");
        }

        return texture;
    }

    private uint MakeGeometry()
    {
        var data = new float[QuadCount * VerticesPerQuad * FloatsPerVertex];
        var offset = 0;

        // Wallpaper: the whole output, texture tiled at 256².
        AddQuad(data, ref offset, 0, 0, _width, _height, 0, 0, _width / 256f, _height / 256f, 255, 255, 255, 255);

        // Window body and title bar: one maximised window in the work area.
        AddQuad(data, ref offset, 240, 180, 2640, 1680, 0, 0, 1, 1, 34, 36, 42, 255);
        AddQuad(data, ref offset, 240, 140, 2640, 180, 0, 0, 1, 1, 56, 59, 68, 255);

        // Cursor at the pointer.
        AddQuad(data, ref offset, 1440, 900, 1472, 932, 0, 0, 1, 1, 255, 255, 255, 255);

        uint buffer;
        Gles.GenBuffers(1, &buffer);
        Gles.BindBuffer(Gles.ArrayBuffer, buffer);
        fixed (float* pointer = data)
        {
            Gles.BufferData(Gles.ArrayBuffer, data.Length * sizeof(float), (nint)pointer, Gles.StaticDraw);
        }

        return buffer;
    }

    private static void AddQuad(float[] data, ref int offset, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, float r, float g, float b, float a)
    {
        Span<(float X, float Y, float U, float V)> corners =
        [
            (x0, y0, u0, v0),
            (x1, y0, u1, v0),
            (x1, y1, u1, v1),
            (x0, y0, u0, v0),
            (x1, y1, u1, v1),
            (x0, y1, u0, v1),
        ];

        foreach (var (x, y, u, v) in corners)
        {
            data[offset++] = x;
            data[offset++] = y;
            data[offset++] = u;
            data[offset++] = v;
            // Vertex colours are normalised: the shader multiplies a 0..1
            // colour by the texture, so the byte values must be divided.
            data[offset++] = r / 255f;
            data[offset++] = g / 255f;
            data[offset++] = b / 255f;
            data[offset++] = a / 255f;
        }
    }

    private static uint MakeWallpaperTexture()
    {
        const int Size = 256;
        var pixels = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var index = ((y * Size) + x) * 4;
                var fx = x / (double)(Size - 1);
                var fy = y / (double)(Size - 1);
                var dx = fx - 0.5;
                var dy = fy - 0.5;
                var vignette = 1.0 - (1.6 * ((dx * dx) + (dy * dy)));
                vignette = Math.Clamp(vignette, 0.15, 1.0);
                var noise = (((x * 37) + (y * 17)) % 7) / 255.0;
                pixels[index] = (byte)Math.Clamp(255 * ((0.10 + (0.55 * fx)) * vignette + noise), 0, 255);
                pixels[index + 1] = (byte)Math.Clamp(255 * ((0.12 + (0.35 * fy)) * vignette + noise), 0, 255);
                pixels[index + 2] = (byte)Math.Clamp(255 * ((0.28 + (0.40 * (1 - fx))) * vignette + noise), 0, 255);
                pixels[index + 3] = 255;
            }
        }

        uint texture;
        Gles.GenTextures(1, &texture);
        Gles.BindTexture(Gles.Texture2D, texture);
        fixed (byte* pointer = pixels)
        {
            Gles.TexImage2D(Gles.Texture2D, 0, unchecked((int)Gles.Rgba8), Size, Size, 0, Gles.Rgba, Gles.UnsignedByte, (nint)pointer);
        }

        Gles.TexParameteri(Gles.Texture2D, Gles.TextureMinFilter, unchecked((int)Gles.Linear));
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureMagFilter, unchecked((int)Gles.Linear));
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureWrapS, unchecked((int)Gles.Repeat));
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureWrapT, unchecked((int)Gles.Repeat));
        return texture;
    }

    private static uint MakeSolidTexture(byte r, byte g, byte b, byte a)
    {
        byte[] pixels = [r, g, b, a];
        uint texture;
        Gles.GenTextures(1, &texture);
        Gles.BindTexture(Gles.Texture2D, texture);
        fixed (byte* pointer = pixels)
        {
            Gles.TexImage2D(Gles.Texture2D, 0, unchecked((int)Gles.Rgba8), 1, 1, 0, Gles.Rgba, Gles.UnsignedByte, (nint)pointer);
        }

        Gles.TexParameteri(Gles.Texture2D, Gles.TextureMinFilter, unchecked((int)Gles.Nearest));
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureMagFilter, unchecked((int)Gles.Nearest));
        return texture;
    }

    private static uint MakeCursorTexture()
    {
        const int Size = 32;
        var pixels = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var index = ((y * Size) + x) * 4;
                var body = (x >= 4 && x < 12 && y >= 2 && y < 28) || (x >= 12 && x < 20 && y >= 6 && y < 20);
                var alpha = body ? (byte)235 : (byte)0;
                pixels[index] = 255;
                pixels[index + 1] = 255;
                pixels[index + 2] = 255;
                pixels[index + 3] = alpha;
            }
        }

        uint texture;
        Gles.GenTextures(1, &texture);
        Gles.BindTexture(Gles.Texture2D, texture);
        fixed (byte* pointer = pixels)
        {
            Gles.TexImage2D(Gles.Texture2D, 0, unchecked((int)Gles.Rgba8), Size, Size, 0, Gles.Rgba, Gles.UnsignedByte, (nint)pointer);
        }

        Gles.TexParameteri(Gles.Texture2D, Gles.TextureMinFilter, unchecked((int)Gles.Nearest));
        Gles.TexParameteri(Gles.Texture2D, Gles.TextureMagFilter, unchecked((int)Gles.Nearest));
        return texture;
    }

    private static uint BuildProgram()
    {
        // ES 3.00 first: Mesa hands back a 3.x context even when 2.0 was
        // requested (and iris rejects the 1.00 dialect there). The 1.00 pair
        // stays as the fallback for a driver that really gives ES2.
        (string Vertex, string Fragment)[] dialects =
        [
            ("""
                #version 300 es
                in vec2 aPosition;
                in vec2 aTexCoord;
                in vec4 aColour;
                uniform vec2 uSize;
                out vec2 vTexCoord;
                out vec4 vColour;
                void main() {
                    vec2 normalised = (aPosition / uSize) * 2.0 - 1.0;
                    gl_Position = vec4(normalised.x, -normalised.y, 0.0, 1.0);
                    vTexCoord = aTexCoord;
                    vColour = aColour;
                }
                """,
                """
                #version 300 es
                precision mediump float;
                uniform sampler2D uTexture;
                in vec2 vTexCoord;
                in vec4 vColour;
                out vec4 fragColour;
                void main() {
                    fragColour = texture(uTexture, vTexCoord) * vColour;
                }
                """),
            ("""
                attribute vec2 aPosition;
                attribute vec2 aTexCoord;
                attribute vec4 aColour;
                uniform vec2 uSize;
                varying vec2 vTexCoord;
                varying vec4 vColour;
                void main() {
                    vec2 normalised = (aPosition / uSize) * 2.0 - 1.0;
                    gl_Position = vec4(normalised.x, -normalised.y, 0.0, 1.0);
                    vTexCoord = aTexCoord;
                    vColour = aColour;
                }
                """,
                """
                precision mediump float;
                uniform sampler2D uTexture;
                varying vec2 vTexCoord;
                varying vec4 vColour;
                void main() {
                    gl_FragColor = texture2D(uTexture, vTexCoord) * vColour;
                }
                """),
        ];

        var failures = new List<string>();
        foreach (var (vertexSource, fragmentSource) in dialects)
        {
            var vertex = Gles.CreateShader(Gles.VertexShader);
            if (vertex == 0)
            {
                failures.Add($"glCreateShader(GL_VERTEX_SHADER) returned 0, glGetError={(Gles.GetError() == 0 ? "none" : $"0x{Gles.GetError():X4}")}");
                continue;
            }

            if (!Compile(vertex, vertexSource, failures))
            {
                Gles.DeleteShader(vertex);
                continue;
            }

            var fragment = Gles.CreateShader(Gles.FragmentShader);
            if (fragment == 0)
            {
                failures.Add($"glCreateShader(GL_FRAGMENT_SHADER) returned 0");
                Gles.DeleteShader(vertex);
                continue;
            }

            if (!Compile(fragment, fragmentSource, failures))
            {
                Gles.DeleteShader(vertex);
                Gles.DeleteShader(fragment);
                continue;
            }

            var program = Gles.CreateProgram();
            Gles.AttachShader(program, vertex);
            Gles.AttachShader(program, fragment);
            Gles.BindAttribLocation(program, 0, "aPosition");
            Gles.BindAttribLocation(program, 1, "aTexCoord");
            Gles.BindAttribLocation(program, 2, "aColour");
            Gles.LinkProgram(program);
            int linked;
            Gles.GetProgramiv(program, Gles.LinkStatus, &linked);
            Gles.DeleteShader(vertex);
            Gles.DeleteShader(fragment);
            if (linked == 0)
            {
                failures.Add($"link: {InfoLog(program, program: true)}");
                Gles.DeleteProgram(program);
                continue;
            }

            return program;
        }

        throw new ProbeException($"no shader dialect compiled: {string.Join(" | ", failures)}");
    }

    private static bool Compile(uint shader, string source, List<string> failures)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(source);
        fixed (byte* text = bytes)
        {
            var pointer = text;
            var length = bytes.Length;
            Gles.ShaderSource(shader, 1, &pointer, &length);
        }

        Gles.CompileShader(shader);
        int compiled;
        Gles.GetShaderiv(shader, Gles.CompileStatus, &compiled);
        if (compiled == 0)
        {
            var log = InfoLog(shader, program: false);
            failures.Add($"compile(type 0x{shader:X}): {(log.Length == 0 ? "(empty log)" : log)}");
            return false;
        }

        return true;
    }

    private static string InfoLog(uint handle, bool program)
    {
        int length;
        if (program)
        {
            Gles.GetProgramiv(handle, Gles.InfoLogLength, &length);
        }
        else
        {
            Gles.GetShaderiv(handle, Gles.InfoLogLength, &length);
        }

        var buffer = new byte[Math.Max(length, 1)];
        fixed (byte* pointer = buffer)
        {
            if (program)
            {
                Gles.GetProgramInfoLog(handle, buffer.Length, null, pointer);
            }
            else
            {
                Gles.GetShaderInfoLog(handle, buffer.Length, null, pointer);
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0');
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Gles.BindFramebuffer(Gles.Framebuffer, 0);
        uint framebuffer = _framebuffer;
        Gles.DeleteFramebuffers(1, &framebuffer);
        Span<uint> textures = [_wallpaper, _white, _cursor];
        fixed (uint* pointer = textures)
        {
            Gles.DeleteTextures(textures.Length, pointer);
        }

        uint buffer = _vbo;
        Gles.DeleteBuffers(1, &buffer);
        Gles.DeleteProgram(_program);
    }
}