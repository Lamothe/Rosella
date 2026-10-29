using System.Globalization;
using Rosella.Control;

// rosella-driver: the outside hand for a running Rosella session. Speaks
// the control socket protocol to capture frames, read state, and inject
// input exactly as an evdev device would deliver it.

var socketPath = ControlClient.DefaultSocketPath;
var argsList = args.ToList();

var socketIndex = argsList.IndexOf("--socket");
if (socketIndex >= 0)
{
    if (socketIndex + 1 >= argsList.Count)
    {
        Console.Error.WriteLine("usage: rosella-driver [--socket PATH] <command> [args]");
        return 2;
    }

    socketPath = argsList[socketIndex + 1];
    argsList.RemoveRange(socketIndex, 2);
}

if (argsList.Count == 0)
{
    Console.Error.WriteLine("""
        usage: rosella-driver [--socket PATH] <command> [args]
          ping                       liveness check
          frame <out.ppm>            capture the composited screen
          table                      surface state table
          state                      outputs, windows, cursor summary
          move <dx> <dy>             relative pointer motion
          warp <x> <y>               warp the cursor (logical coords)
          button <code> <0|1>        pointer button down/up
          key <code> <0|1>           key down/up (evdev codes)
          click <code>               button down+up (e.g. 272 = left)
          tap-key <code>             key down+up (e.g. 28 = Enter)
          blank                      force the screen dark (DPMS off)
          unblank                    force the screen lit
        """);
    return 2;
}

// One command per line: an embedded newline would smuggle a second
// command and desync the answers.
if (argsList.Any(a => a.Contains('\n') || a.Contains('\r')))
{
    Console.Error.WriteLine("error: arguments must not contain newlines");
    return 2;
}

var command = argsList[0];
var rest = argsList.Skip(1).ToArray();

try
{
    using var client = new ControlClient(socketPath);

    switch (command)
    {
        case "ping":
        case "table":
        case "state":
        {
            var (ok, text) = Ask(client, string.Join(' ', new[] { command }.Concat(rest)));
            if (!ok)
            {
                Console.Error.WriteLine("error: " + text);
                return 1;
            }

            Console.Write(text);
            return 0;
        }

        case "frame":
        {
            if (rest.Length != 1)
            {
                Console.Error.WriteLine("usage: rosella-driver frame <out.ppm>");
                return 2;
            }

            var (ok, payload) = client.Send("frame");
            if (!ok)
            {
                Console.Error.WriteLine("error: " + System.Text.Encoding.UTF8.GetString(payload));
                return 1;
            }

            File.WriteAllBytes(rest[0], payload);
            Console.WriteLine($"{rest[0]}: {payload.Length} bytes");
            return 0;
        }

        case "move":
        case "warp":
        {
            if (rest.Length != 2)
            {
                Console.Error.WriteLine($"usage: rosella-driver {command} <x> <y>");
                return 2;
            }

            return OkOrError(client, $"{command} {rest[0]} {rest[1]}");
        }

        case "button":
        case "key":
        {
            if (rest.Length != 2)
            {
                Console.Error.WriteLine($"usage: rosella-driver {command} <code> <0|1>");
                return 2;
            }

            return OkOrError(client, $"{command} {rest[0]} {rest[1]}");
        }

        case "click":
        {
            if (rest.Length != 1 || !int.TryParse(rest[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                Console.Error.WriteLine("usage: rosella-driver click <button-code>");
                return 2;
            }

            var r = OkOrError(client, $"button {code} 1");
            return r == 0 ? OkOrError(client, $"button {code} 0") : r;
        }

        case "tap-key":
        {
            if (rest.Length != 1 || !int.TryParse(rest[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                Console.Error.WriteLine("usage: rosella-driver tap-key <key-code>");
                return 2;
            }

            var r = OkOrError(client, $"key {code} 1");
            return r == 0 ? OkOrError(client, $"key {code} 0") : r;
        }

        case "blank":
        case "unblank":
        {
            if (rest.Length != 0)
            {
                Console.Error.WriteLine($"usage: rosella-driver {command}");
                return 2;
            }

            var (ok, payload) = client.Send(command);
            if (!ok)
            {
                Console.Error.WriteLine("error: " + System.Text.Encoding.UTF8.GetString(payload));
                return 1;
            }

            Console.Write(System.Text.Encoding.UTF8.GetString(payload));
            return 0;
        }

        default:
            Console.Error.WriteLine($"unknown command: {command}");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static (bool Ok, string Text) Ask(ControlClient client, string command)
{
    var (ok, payload) = client.Send(command);
    return (ok, System.Text.Encoding.UTF8.GetString(payload));
}

static int OkOrError(ControlClient client, string command)
{
    var (ok, payload) = client.Send(command);
    if (!ok)
    {
        Console.Error.WriteLine("error: " + System.Text.Encoding.UTF8.GetString(payload));
        return 1;
    }

    return 0;
}
