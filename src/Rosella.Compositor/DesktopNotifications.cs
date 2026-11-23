using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>Serves session-bus notifications without replacing an existing desktop's owner.</summary>
internal sealed unsafe class DesktopNotifications : IDisposable
{
    private const string Freedesktop = "org.freedesktop.Notifications";
    private const string FreedesktopPath = "/org/freedesktop/Notifications";
    private const string Gtk = "org.gtk.Notifications";
    private const string GtkPath = "/org/gtk/Notifications";
    private const string Introspectable = "org.freedesktop.DBus.Introspectable";
    private const string FreedesktopXml = """
        <node>
          <interface name="org.freedesktop.Notifications">
            <method name="GetCapabilities"><arg type="as" direction="out"/></method>
            <method name="GetServerInformation">
              <arg type="s" direction="out"/><arg type="s" direction="out"/>
              <arg type="s" direction="out"/><arg type="s" direction="out"/>
            </method>
            <method name="Notify">
              <arg name="app_name" type="s" direction="in"/><arg name="replaces_id" type="u" direction="in"/>
              <arg name="app_icon" type="s" direction="in"/><arg name="summary" type="s" direction="in"/>
              <arg name="body" type="s" direction="in"/><arg name="actions" type="as" direction="in"/>
              <arg name="hints" type="a{sv}" direction="in"/><arg name="expire_timeout" type="i" direction="in"/>
              <arg name="id" type="u" direction="out"/>
            </method>
            <method name="CloseNotification"><arg name="id" type="u" direction="in"/></method>
            <signal name="NotificationClosed"><arg type="u"/><arg type="u"/></signal>
          </interface>
        </node>
        """;
    private const string GtkXml = """
        <node>
          <interface name="org.gtk.Notifications">
            <method name="AddNotification">
              <arg name="application" type="s" direction="in"/><arg name="id" type="s" direction="in"/>
              <arg name="notification" type="a{sv}" direction="in"/>
            </method>
            <method name="RemoveNotification">
              <arg name="application" type="s" direction="in"/><arg name="id" type="s" direction="in"/>
            </method>
          </interface>
        </node>
        """;

    private readonly NotificationStack _toasts;
    private readonly ConcurrentQueue<(uint Id, NotificationCloseReason Reason)> _closed = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    internal DesktopNotifications(NotificationStack toasts)
    {
        _toasts = toasts;
        _toasts.Closed = (id, reason) => _closed.Enqueue((id, reason));
        _thread = new Thread(Run) { IsBackground = true, Name = "rosella-notifications" };
        _thread.Start();
    }

    private void Run()
    {
        nint bus = 0;
        try
        {
            Check(sd_bus_open_user(out bus));
            Check(sd_bus_set_method_call_timeout(bus, 1_000_000));
            var ownsFreedesktop = false;
            var ownsGtk = false;
            var nextClaim = 0L;
            while (!_stop.IsCancellationRequested)
            {
                var now = Environment.TickCount64;
                if (now >= nextClaim)
                {
                    ownsFreedesktop = ClaimName(bus, Freedesktop, ownsFreedesktop, announceBusy: nextClaim == 0);
                    ownsGtk = ClaimName(bus, Gtk, ownsGtk, announceBusy: nextClaim == 0);
                    nextClaim = now + 2000;
                }

                _toasts.Expire();
                while (_closed.TryDequeue(out var closed))
                {
                    SendClosed(bus, closed.Id, closed.Reason);
                }

                var result = sd_bus_process(bus, out var message);
                Check(result);
                if (message != 0)
                {
                    try
                    {
                        Handle(bus, message, ownsFreedesktop, ownsGtk);
                    }
                    finally
                    {
                        sd_bus_message_unref(message);
                    }
                }
                else if (result == 0)
                {
                    // Bound expiry and shutdown while the bus is idle.
                    Check(sd_bus_wait(bus, 100_000));
                }
            }
        }
        catch (Exception error) when (error is IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"rosella: desktop notifications unavailable ({error.Message})");
        }
        finally
        {
            if (bus != 0)
            {
                sd_bus_close_unref(bus);
            }
        }
    }

    private static bool ClaimName(nint bus, string name, bool alreadyOwned, bool announceBusy)
    {
        if (alreadyOwned)
        {
            return true;
        }

        var result = sd_bus_request_name(bus, name, 0);
        if (result >= 0)
        {
            Console.WriteLine($"rosella: serving {name}");
            return true;
        }

        if (announceBusy)
        {
            Console.Error.WriteLine($"rosella: waiting for notification bus name {name} ({result})");
        }
        return false;
    }

    private void Handle(nint bus, nint message, bool ownsFreedesktop, bool ownsGtk)
    {
        if (sd_bus_message_get_type(message, out var kind) < 0 || kind != 1)
        {
            return;
        }

        var path = Text(sd_bus_message_get_path(message));
        var isFreedesktop = ownsFreedesktop && path == FreedesktopPath;
        if (!isFreedesktop && !(ownsGtk && path == GtkPath))
        {
            return;
        }

        var iface = Text(sd_bus_message_get_interface(message));
        var method = Text(sd_bus_message_get_member(message));
        if (iface != (isFreedesktop ? Freedesktop : Gtk) &&
            !(iface == Introspectable && method == "Introspect"))
        {
            return;
        }

        try
        {
            Check(sd_bus_message_new_method_return(message, out var reply));
            try
            {
                if (iface == Introspectable)
                {
                    Append(reply, isFreedesktop ? FreedesktopXml : GtkXml);
                }
                else if (isFreedesktop)
                {
                    HandleFreedesktop(message, reply, method);
                }
                else
                {
                    HandleGtk(message, method);
                }

                Check(sd_bus_send(bus, reply, 0));
            }
            finally
            {
                sd_bus_message_unref(reply);
            }
        }
        catch (IOException)
        {
            // Malformed method arguments must not take down the notification service.
            sd_bus_reply_method_errorf(message, "org.freedesktop.DBus.Error.InvalidArgs", "Invalid notification request");
        }
    }

    private void HandleFreedesktop(nint message, nint reply, string method)
    {
        switch (method)
        {
            case "GetCapabilities":
                Check(sd_bus_message_open_container(reply, (byte)'a', "s"));
                Append(reply, "body");
                Check(sd_bus_message_close_container(reply));
                break;
            case "GetServerInformation":
                Append(reply, "Rosella");
                Append(reply, "Rosella");
                Append(reply, "1.0");
                Append(reply, "1.2");
                break;
            case "Notify":
                var app = ReadString(message);
                var replacement = ReadUInt(message, (byte)'u');
                ReadString(message); // App icon
                var title = ReadString(message);
                var body = ReadString(message);
                Require(sd_bus_message_skip(message, "asa{sv}"));
                var timeout = unchecked((int)ReadUInt(message, (byte)'i'));
                var key = _toasts.Show(new NotificationKey(replacement), app, title, body, timeout);
                Append(reply, key.Number);
                break;
            case "CloseNotification":
                _toasts.Close(new NotificationKey(ReadUInt(message, (byte)'u')));
                break;
            default:
                throw new IOException("unknown notifications method");
        }
    }

    private void HandleGtk(nint message, string method)
    {
        var application = ReadString(message);
        var token = ReadString(message);
        var key = new NotificationKey(0, application, token);
        switch (method)
        {
            case "AddNotification":
                Require(sd_bus_message_enter_container(message, (byte)'a', "{sv}"));
                var title = "";
                var body = "";
                while (Check(sd_bus_message_enter_container(message, (byte)'e', "sv")) > 0)
                {
                    var name = ReadString(message);
                    var isText = Check(sd_bus_message_peek_type(message, out _, out var contents)) > 0 &&
                        Text(contents) == "s";
                    if (isText && name is "title" or "body")
                    {
                        Require(sd_bus_message_enter_container(message, (byte)'v', "s"));
                        var value = ReadString(message);
                        Check(sd_bus_message_exit_container(message));
                        if (name == "title")
                        {
                            title = value;
                        }
                        else
                        {
                            body = value;
                        }
                    }
                    else
                    {
                        Require(sd_bus_message_skip(message, "v"));
                    }
                    Check(sd_bus_message_exit_container(message));
                }
                Check(sd_bus_message_exit_container(message));
                var app = application[(application.LastIndexOf('.') + 1)..];
                _toasts.Show(key, app, title, body, -1);
                break;
            case "RemoveNotification":
                _toasts.Close(key);
                break;
            default:
                throw new IOException("unknown GTK notifications method");
        }
    }

    private static void SendClosed(nint bus, uint id, NotificationCloseReason reason)
    {
        Check(sd_bus_message_new_signal(bus, out var signal, FreedesktopPath, Freedesktop, "NotificationClosed"));
        try
        {
            Append(signal, id);
            Append(signal, (uint)reason);
            Check(sd_bus_send(bus, signal, 0));
        }
        finally
        {
            sd_bus_message_unref(signal);
        }
    }

    public void Dispose()
    {
        _toasts.Closed = null;
        _stop.Cancel();
        _thread.Join();
        _stop.Dispose();
    }

    private static string ReadString(nint message)
    {
        nint value = 0;
        Require(sd_bus_message_read_basic(message, (byte)'s', (nint)(&value)));
        return Text(value);
    }

    private static uint ReadUInt(nint message, byte type)
    {
        uint value = 0;
        Require(sd_bus_message_read_basic(message, type, (nint)(&value)));
        return value;
    }

    private static string Text(nint value) => Marshal.PtrToStringUTF8(value) ?? "";
    private static void Append(nint message, string text) => Check(AppendString(message, (byte)'s', text));
    private static void Append(nint message, uint value) => Check(sd_bus_message_append_basic(message, (byte)'u', (nint)(&value)));

    private static int Check(int result) =>
        result < 0 ? throw new IOException($"sd-bus error {result}") : result;

    private static void Require(int result)
    {
        if (Check(result) == 0)
        {
            throw new IOException("notification arguments ended early");
        }
    }

    private const string Systemd = "libsystemd.so.0";
    [DllImport(Systemd)] private static extern int sd_bus_open_user(out nint bus);
    [DllImport(Systemd)] private static extern nint sd_bus_close_unref(nint bus);
    [DllImport(Systemd)] private static extern int sd_bus_set_method_call_timeout(nint bus, ulong microseconds);
    [DllImport(Systemd)] private static extern int sd_bus_request_name(nint bus, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ulong flags);
    [DllImport(Systemd)] private static extern int sd_bus_wait(nint bus, ulong microseconds);
    [DllImport(Systemd)] private static extern int sd_bus_process(nint bus, out nint message);
    [DllImport(Systemd)] private static extern int sd_bus_send(nint bus, nint message, nint cookie);
    [DllImport(Systemd)] private static extern nint sd_bus_message_unref(nint message);
    [DllImport(Systemd)] private static extern int sd_bus_message_get_type(nint message, out byte kind);
    [DllImport(Systemd)] private static extern nint sd_bus_message_get_path(nint message);
    [DllImport(Systemd)] private static extern nint sd_bus_message_get_interface(nint message);
    [DllImport(Systemd)] private static extern nint sd_bus_message_get_member(nint message);
    [DllImport(Systemd)] private static extern int sd_bus_message_new_method_return(nint message, out nint reply);
    [DllImport(Systemd)] private static extern int sd_bus_message_new_signal(nint bus, out nint message,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string iface, [MarshalAs(UnmanagedType.LPUTF8Str)] string member);
    [DllImport(Systemd)] private static extern int sd_bus_message_read_basic(nint message, byte type, nint output);
    [DllImport(Systemd)] private static extern int sd_bus_message_append_basic(nint message, byte type, nint value);
    [DllImport(Systemd, EntryPoint = "sd_bus_message_append_basic")]
    private static extern int AppendString(nint message, byte type, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Systemd)] private static extern int sd_bus_message_skip(nint message, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [DllImport(Systemd)] private static extern int sd_bus_message_open_container(nint message, byte type, [MarshalAs(UnmanagedType.LPUTF8Str)] string contents);
    [DllImport(Systemd)] private static extern int sd_bus_message_close_container(nint message);
    [DllImport(Systemd)] private static extern int sd_bus_message_enter_container(nint message, byte type, [MarshalAs(UnmanagedType.LPUTF8Str)] string contents);
    [DllImport(Systemd)] private static extern int sd_bus_message_exit_container(nint message);
    [DllImport(Systemd)] private static extern int sd_bus_message_peek_type(nint message, out byte type, out nint contents);
    [DllImport(Systemd)] private static extern int sd_bus_reply_method_errorf(nint message,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string format);
}
