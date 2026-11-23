using System.Runtime.InteropServices;

namespace Rosella.GpuProbe;

/// <summary>
/// OpenGL ES 2.0 entry points, imported straight from libGLESv2. The probe
/// deliberately renders with an ES 2 context: it is the lowest common
/// denominator Rosella could target, and each draw here is a textured quad,
/// so the API generation changes nothing about the fill cost being measured.
/// </summary>
internal static unsafe class Gles
{
    private const string Lib = "libGLESv2.so.2";

    internal const uint DepthBufferBit = 0x00000100;
    internal const uint ColorBufferBit = 0x00004000;
    internal const uint Texture2D = 0x0DE1;
    internal const uint Blend = 0x0BE2;
    internal const uint SrcAlpha = 0x0302;
    internal const uint OneMinusSrcAlpha = 0x0303;
    internal const uint Rgba = 0x1908;
    internal const uint Bgra = 0x80E1;
    internal const uint Rgba8 = 0x8058;
    internal const uint UnsignedByte = 0x1401;
    internal const uint Float = 0x1406;
    internal const uint TextureMinFilter = 0x2801;
    internal const uint TextureMagFilter = 0x2800;
    internal const uint TextureWrapS = 0x2802;
    internal const uint TextureWrapT = 0x2803;
    internal const uint Linear = 0x2601;
    internal const uint Nearest = 0x2600;
    internal const uint Repeat = 0x2901;
    internal const uint ClampToEdge = 0x812F;
    internal const uint Framebuffer = 0x8D40;
    internal const uint ColorAttachment0 = 0x8CE0;
    internal const uint FramebufferComplete = 0x8CD5;
    internal const uint ArrayBuffer = 0x8892;
    internal const uint PixelPackBuffer = 0x88EB;
    internal const uint StreamRead = 0x88E1;
    internal const uint MapReadBit = 0x0001;
    internal const uint StaticDraw = 0x88E4;
    internal const uint VertexShader = 0x8B31;
    internal const uint FragmentShader = 0x8B30;
    internal const uint CompileStatus = 0x8B81;
    internal const uint LinkStatus = 0x8B82;
    internal const uint InfoLogLength = 0x8B84;
    internal const uint Vendor = 0x1F00;
    internal const uint Renderer = 0x1F01;
    internal const uint VersionString = 0x1F02;
    internal const uint ShadingLanguageVersion = 0x8B8C;
    internal const uint Extensions = 0x1F03;
    internal const uint MaxTextureSize = 0x0D33;
    internal const uint MaxViewportDims = 0x0D3A;
    internal const uint NoError = 0;

    [DllImport(Lib, EntryPoint = "glGetString")]
    internal static extern nint GetString(uint name);

    [DllImport(Lib, EntryPoint = "glGetIntegerv")]
    internal static extern void GetIntegerv(uint name, int* value);

    [DllImport(Lib, EntryPoint = "glGetError")]
    internal static extern uint GetError();

    [DllImport(Lib, EntryPoint = "glViewport")]
    internal static extern void Viewport(int x, int y, int width, int height);

    [DllImport(Lib, EntryPoint = "glClearColor")]
    internal static extern void ClearColor(float red, float green, float blue, float alpha);

    [DllImport(Lib, EntryPoint = "glClear")]
    internal static extern void Clear(uint mask);

    [DllImport(Lib, EntryPoint = "glEnable")]
    internal static extern void Enable(uint cap);

    [DllImport(Lib, EntryPoint = "glDisable")]
    internal static extern void Disable(uint cap);

    [DllImport(Lib, EntryPoint = "glBlendFunc")]
    internal static extern void BlendFunc(uint source, uint destination);

    [DllImport(Lib, EntryPoint = "glFinish")]
    internal static extern void Finish();

    [DllImport(Lib, EntryPoint = "glFlush")]
    internal static extern void Flush();

    [DllImport(Lib, EntryPoint = "glPixelStorei")]
    internal static extern void PixelStorei(uint pname, int value);

    [DllImport(Lib, EntryPoint = "glGenTextures")]
    internal static extern void GenTextures(int count, uint* textures);

    [DllImport(Lib, EntryPoint = "glDeleteTextures")]
    internal static extern void DeleteTextures(int count, uint* textures);

    [DllImport(Lib, EntryPoint = "glBindTexture")]
    internal static extern void BindTexture(uint target, uint texture);

    [DllImport(Lib, EntryPoint = "glTexImage2D")]
    internal static extern void TexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, nint pixels);

    [DllImport(Lib, EntryPoint = "glTexParameteri")]
    internal static extern void TexParameteri(uint target, uint pname, int parameter);

    [DllImport(Lib, EntryPoint = "glGenFramebuffers")]
    internal static extern void GenFramebuffers(int count, uint* framebuffers);

    [DllImport(Lib, EntryPoint = "glDeleteFramebuffers")]
    internal static extern void DeleteFramebuffers(int count, uint* framebuffers);

    [DllImport(Lib, EntryPoint = "glBindFramebuffer")]
    internal static extern void BindFramebuffer(uint target, uint framebuffer);

    [DllImport(Lib, EntryPoint = "glFramebufferTexture2D")]
    internal static extern void FramebufferTexture2D(uint target, uint attachment, uint textarget, uint texture, int level);

    [DllImport(Lib, EntryPoint = "glCheckFramebufferStatus")]
    internal static extern uint CheckFramebufferStatus(uint target);

    [DllImport(Lib, EntryPoint = "glGenBuffers")]
    internal static extern void GenBuffers(int count, uint* buffers);

    [DllImport(Lib, EntryPoint = "glDeleteBuffers")]
    internal static extern void DeleteBuffers(int count, uint* buffers);

    [DllImport(Lib, EntryPoint = "glBindBuffer")]
    internal static extern void BindBuffer(uint target, uint buffer);

    [DllImport(Lib, EntryPoint = "glBufferData")]
    internal static extern void BufferData(uint target, nint size, nint data, uint usage);

    [DllImport(Lib, EntryPoint = "glCreateShader")]
    internal static extern uint CreateShader(uint type);

    [DllImport(Lib, EntryPoint = "glDeleteShader")]
    internal static extern void DeleteShader(uint shader);

    [DllImport(Lib, EntryPoint = "glShaderSource")]
    internal static extern void ShaderSource(uint shader, int count, byte** strings, int* lengths);

    [DllImport(Lib, EntryPoint = "glCompileShader")]
    internal static extern void CompileShader(uint shader);

    [DllImport(Lib, EntryPoint = "glGetShaderiv")]
    internal static extern void GetShaderiv(uint shader, uint pname, int* value);

    [DllImport(Lib, EntryPoint = "glGetShaderInfoLog")]
    internal static extern void GetShaderInfoLog(uint shader, int bufferSize, int* length, byte* infoLog);

    [DllImport(Lib, EntryPoint = "glCreateProgram")]
    internal static extern uint CreateProgram();

    [DllImport(Lib, EntryPoint = "glBindAttribLocation")]
    internal static extern void BindAttribLocation(uint program, uint index, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib, EntryPoint = "glDeleteProgram")]
    internal static extern void DeleteProgram(uint program);

    [DllImport(Lib, EntryPoint = "glAttachShader")]
    internal static extern void AttachShader(uint program, uint shader);

    [DllImport(Lib, EntryPoint = "glLinkProgram")]
    internal static extern void LinkProgram(uint program);

    [DllImport(Lib, EntryPoint = "glGetProgramiv")]
    internal static extern void GetProgramiv(uint program, uint pname, int* value);

    [DllImport(Lib, EntryPoint = "glGetProgramInfoLog")]
    internal static extern void GetProgramInfoLog(uint program, int bufferSize, int* length, byte* infoLog);

    [DllImport(Lib, EntryPoint = "glUseProgram")]
    internal static extern void UseProgram(uint program);

    [DllImport(Lib, EntryPoint = "glGetUniformLocation")]
    internal static extern int GetUniformLocation(uint program, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib, EntryPoint = "glUniform1i")]
    internal static extern void Uniform1i(int location, int value);

    [DllImport(Lib, EntryPoint = "glUniform2f")]
    internal static extern void Uniform2f(int location, float x, float y);

    [DllImport(Lib, EntryPoint = "glVertexAttribPointer")]
    internal static extern void VertexAttribPointer(uint index, int size, uint type, byte normalized, int stride, nint pointer);

    [DllImport(Lib, EntryPoint = "glEnableVertexAttribArray")]
    internal static extern void EnableVertexAttribArray(uint index);

    [DllImport(Lib, EntryPoint = "glDrawArrays")]
    internal static extern void DrawArrays(uint mode, int first, int count);

    [DllImport(Lib, EntryPoint = "glMapBufferRange")]
    internal static extern nint MapBufferRange(uint target, nint offset, nint length, uint access);

    [DllImport(Lib, EntryPoint = "glUnmapBuffer")]
    internal static extern byte UnmapBuffer(uint target);

    [DllImport(Lib, EntryPoint = "glReadPixels")]
    internal static extern void ReadPixels(int x, int y, int width, int height, uint format, uint type, nint pixels);

    [DllImport(Lib, EntryPoint = "glFinish")]
    internal static extern void FinishNoOp();
}