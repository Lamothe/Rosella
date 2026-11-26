using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>Owns one authenticated rootless X display for a desktop.</summary>
internal sealed class XWaylandSession : IDisposable
{
    private readonly DirectoryInfo _directory;
    private readonly DisplayServer _server;
    private readonly Process _helper;
    private readonly string _display;
    private readonly string _authority;
    private volatile bool _disposed;

    private XWaylandSession(DirectoryInfo directory, DisplayServer server, Process helper, string display, string authority, Task exited)
    {
        _directory = directory;
        _server = server;
        _helper = helper;
        _display = display;
        _authority = authority;
        Exited = exited;
    }

    internal Task Exited { get; }

    internal static string? FindHelper()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "libexec", "xwayland-satellite");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator);
        return paths?.Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, "xwayland-satellite"))
            .FirstOrDefault(File.Exists);
    }

    internal static XWaylandSession? Start(string executable, Scene scene, SeatState seat, OutputInfo output, DmaBufReader? dmaBuf)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        DirectoryInfo? directory = null;
        DisplayServer? server = null;
        Process? helper = null;
        var started = false;
        try
        {
            directory = Directory.CreateTempSubdirectory("rosella-x11-");
            var authority = Path.Combine(directory.FullName, "Xauthority");
            WriteAuthority(authority);
            // Only the bridge receives the DMA-BUF global on this socket.
            // Native clients use the normal desktop socket and its existing policy.
            var factory = new CompositorHandlerFactory(scene, seat, output, dmaBuf, includeXwaylandProtocols: true);
            server = new DisplayServer(Path.Combine(directory.FullName, "wayland"), factory.Globals, factory);
            server.Start();

            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true };
            start.ArgumentList.Add("-auth");
            start.ArgumentList.Add(authority);
            start.ArgumentList.Add("-nolisten");
            start.ArgumentList.Add("tcp");
            start.Environment["XDG_RUNTIME_DIR"] = directory.FullName;
            start.Environment["WAYLAND_DISPLAY"] = server.SocketPath;
            start.Environment.Remove("WAYLAND_SOCKET");
            start.Environment["XAUTHORITY"] = authority;
            start.Environment["RUST_LOG"] = "info";
            var ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            helper = new Process { StartInfo = start, EnableRaisingEvents = true };
            helper.Exited += (_, _) =>
            {
                exited.TrySetResult();
                ready.TrySetResult(null);
            };
            helper.ErrorDataReceived += (_, message) =>
            {
                if (message.Data is not { } line)
                {
                    return;
                }
                Console.Error.WriteLine(line);
                const string marker = "Connected to Xwayland on :";
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0 && uint.TryParse(line.AsSpan(index + marker.Length), out var number))
                {
                    ready.TrySetResult($":{number}");
                }
            };
            helper.Start();
            started = true;
            helper.BeginErrorReadLine();
            if (!ready.Task.Wait(TimeSpan.FromSeconds(4)) || ready.Task.Result is not { } display || helper.HasExited)
            {
                throw new InvalidOperationException("the X11 display did not become ready");
            }
            return new XWaylandSession(directory, server, helper, display, authority, exited.Task);
        }
        catch (Exception exception) when (exception is IOException or SocketException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"rosella: XWayland unavailable: {exception.Message}");
            Stop(directory, server, helper, started);
            return null;
        }
    }

    internal bool ApplyTo(ProcessStartInfo start)
    {
        if (_disposed || Exited.IsCompleted)
        {
            return false;
        }

        start.Environment["DISPLAY"] = _display;
        start.Environment["XAUTHORITY"] = _authority;
        return true;
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteAuthority(string path)
    {
        using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        // Xwayland assigns the display number atomically. The file is
        // private and passed only to that server and its authorized clients.
        WriteNumber(file, ushort.MaxValue); // Xauthority FamilyWild
        WriteField(file, []); // address
        WriteField(file, []); // display number
        WriteField(file, Encoding.ASCII.GetBytes("MIT-MAGIC-COOKIE-1"));
        WriteField(file, RandomNumberGenerator.GetBytes(16));
    }

    private static void WriteNumber(Stream file, ushort number)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, number);
        file.Write(bytes);
    }

    private static void WriteField(Stream file, ReadOnlySpan<byte> bytes)
    {
        WriteNumber(file, (ushort)bytes.Length);
        file.Write(bytes);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Stop(_directory, _server, _helper, started: true);
        }
    }

    private static void Stop(DirectoryInfo? directory, DisplayServer? server, Process? helper, bool started)
    {
        try
        {
            server?.Dispose();
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"rosella: could not remove the private Wayland socket: {exception.Message}");
        }
        using (helper)
        {
            if (started && helper is not null && !helper.WaitForExit(750))
            {
                try
                {
                    helper.Kill(entireProcessTree: true);
                    helper.WaitForExit(750);
                }
                catch (InvalidOperationException) when (helper.HasExited)
                {
                }
                catch (Win32Exception exception)
                {
                    Console.Error.WriteLine($"rosella: could not stop XWayland: {exception.Message}");
                }
            }
        }
        try
        {
            directory?.Delete(recursive: true);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"rosella: could not remove the XWayland runtime directory: {exception.Message}");
        }
    }
}
