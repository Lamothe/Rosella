using System.Net.Sockets;
using System.Text;
using Rosella.Control;

namespace Rosella.Control.Tests;

/// <summary>Records what the server drives and answers with canned payloads.</summary>
internal sealed class FakeTarget : IControlTarget
{
    public List<string> Calls { get; } = new();
    public byte[] FramePayload { get; set; } = [0x50, 0x36, 0x00, 0x0A, 0xFF]; // P6, NUL, newline, 0xFF
    public string Table { get; set; } = "surface 1\nsurface 2";
    public string StateText { get; set; } = "output 1x1\ncursor 0,0";
    public string? ThrowOn { get; set; }

    public byte[] CaptureFrame()
    {
        Calls.Add("frame");
        MaybeThrow();
        return FramePayload;
    }

    public string SurfaceTable()
    {
        Calls.Add("table");
        MaybeThrow();
        return Table;
    }

    public string State()
    {
        Calls.Add("state");
        MaybeThrow();
        return StateText;
    }

    public void RelativeMotion(int dx, int dy)
    {
        Calls.Add($"move {dx} {dy}");
        MaybeThrow();
    }

    public void WarpCursor(int x, int y)
    {
        Calls.Add($"warp {x} {y}");
        MaybeThrow();
    }

    public void Button(ushort code, int value)
    {
        Calls.Add($"button {code} {value}");
        MaybeThrow();
    }

    public void Key(ushort code, int value)
    {
        Calls.Add($"key {code} {value}");
        MaybeThrow();
    }

    public string SetBlanked(bool blank)
    {
        Calls.Add(blank ? "blank" : "unblank");
        MaybeThrow();
        return blank ? "blanked" : "awake";
    }

    private void MaybeThrow()
    {
        if (ThrowOn is not null && Calls[^1].StartsWith(ThrowOn, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("target exploded");
        }
    }
}

public sealed class ControlServerTests : IDisposable
{
    private readonly string _path = $"/tmp/rosella-control-test-{Guid.NewGuid():N}.sock";

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private (ControlServer Server, FakeTarget Target, ControlClient Client) Start()
    {
        var target = new FakeTarget();
        var server = new ControlServer(_path, target);
        var client = new ControlClient(_path);
        return (server, target, client);
    }

    [Fact]
    public void Ping_round_trips()
    {
        var (server, _, client) = Start();
        using (server)
        using (client)
        {
            var (ok, payload) = client.Send("ping");
            Assert.True(ok);
            Assert.Equal("pong", Encoding.UTF8.GetString(payload));
        }
    }

    [Fact]
    public void Blank_and_unblank_drive_the_target()
    {
        var (server, target, client) = Start();
        using (server)
        using (client)
        {
            var (ok, payload) = client.Send("blank");
            Assert.True(ok);
            Assert.Equal("blanked", Encoding.UTF8.GetString(payload).TrimEnd('\n'));

            var (ok2, payload2) = client.Send("unblank");
            Assert.True(ok2);
            Assert.Equal("awake", Encoding.UTF8.GetString(payload2).TrimEnd('\n'));

            Assert.Equal(new[] { "blank", "unblank" }, target.Calls);
        }
    }

    [Fact]
    public void Blank_takes_no_arguments()
    {
        var (server, target, client) = Start();
        using (server)
        using (client)
        {
            var (ok, _) = client.Send("blank now");
            Assert.False(ok);
            Assert.Empty(target.Calls);
        }
    }

    [Fact]
    public void Frame_payload_survives_binary_content()
    {
        var (server, target, client) = Start();
        using (server)
        using (client)
        {
            var (ok, payload) = client.Send("frame");
            Assert.True(ok);
            Assert.Equal(target.FramePayload, payload); // NUL, newline and 0xFF all intact
            Assert.Equal(new[] { "frame" }, target.Calls);
        }
    }

    [Fact]
    public void Text_commands_carry_multiline_payloads()
    {
        var (server, target, client) = Start();
        using (server)
        using (client)
        {
            var (ok, table) = client.Send("table");
            Assert.True(ok);
            Assert.Equal(target.Table, Encoding.UTF8.GetString(table));

            var (ok2, state) = client.Send("state");
            Assert.True(ok2);
            Assert.Equal(target.StateText, Encoding.UTF8.GetString(state));
        }
    }

    [Theory]
    [InlineData("move 10 -3", "move 10 -3")]
    [InlineData("warp 640 480", "warp 640 480")]
    [InlineData("button 272 1", "button 272 1")]
    [InlineData("button 272 0", "button 272 0")]
    [InlineData("key 28 1", "key 28 1")]
    [InlineData("key 28 0", "key 28 0")]
    public void Input_commands_reach_the_target(string command, string expectedCall)
    {
        var (server, target, client) = Start();
        using (server)
        using (client)
        {
            var (ok, _) = client.Send(command);
            Assert.True(ok);
            Assert.Equal(new[] { expectedCall }, target.Calls);
        }
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("move 1 x")]
    [InlineData("move 1")]
    [InlineData("warp 1")]
    [InlineData("button 272 2")]
    [InlineData("button -1 1")]
    [InlineData("button 70000 1")]
    [InlineData("key 28")]
    public void Malformed_requests_answer_err(string command)
    {
        var (server, target, client) = Start();
        using (server)
        using (client)
        {
            var (ok, payload) = client.Send(command);
            Assert.False(ok);
            Assert.NotEmpty(payload);
            Assert.Empty(target.Calls);
        }
    }

    [Fact]
    public void Target_exceptions_become_err_answers_and_the_connection_survives()
    {
        var (server, target, client) = Start();
        using (server)
        using (client)
        {
            target.ThrowOn = "table";
            var (ok, payload) = client.Send("table");
            Assert.False(ok);
            Assert.Contains("exploded", Encoding.UTF8.GetString(payload));

            var (ok2, _) = client.Send("ping");
            Assert.True(ok2); // still served
        }
    }

    [Fact]
    public void One_wedged_client_does_not_block_the_next()
    {
        var (server, _, client) = Start();
        using (server)
        using (client)
        {
            // 'client' connects and sends nothing at all.
            using var second = new ControlClient(_path);
            var (ok, _) = second.Send("ping");
            Assert.True(ok);
        }
    }

    [Fact]
    public void An_oversized_request_line_is_refused()
    {
        var (server, _, _) = Start();
        using (server)
        {
            using var raw = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            raw.Connect(new UnixDomainSocketEndPoint(_path));
            raw.Send(Encoding.ASCII.GetBytes(new string('x', 512) + "\n"));

            var answer = new byte[256];
            var n = raw.Receive(answer);
            var text = Encoding.UTF8.GetString(answer, 0, n);
            Assert.StartsWith("ERR", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_wire_format_is_exactly_length_prefixed()
    {
        var (server, _, _) = Start();
        using (server)
        {
            using var raw = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            raw.Connect(new UnixDomainSocketEndPoint(_path));
            raw.Send(Encoding.ASCII.GetBytes("ping\n"));

            // Read until the full expected answer arrives: the server may
            // legitimately split it across sends.
            var expected = "OK 4\npong"u8.ToArray();
            var answer = new byte[expected.Length];
            var read = 0;
            while (read < answer.Length)
            {
                read += raw.Receive(answer, read, answer.Length - read, SocketFlags.None);
            }

            Assert.Equal(expected, answer);
        }
    }

    [Fact]
    public void A_late_dispose_never_unlinks_a_replacement_servers_socket()
    {
        var serverA = new ControlServer(_path, new FakeTarget());
        serverA.Dispose();

        // A replacement takes the path over.
        using var serverB = new ControlServer(_path, new FakeTarget());

        // The old instance's cleanup runs again, late.
        serverA.Dispose();

        using var client = new ControlClient(_path);
        var (ok, _) = client.Send("ping");
        Assert.True(ok); // serverB's socket survived the late delete
    }

    [Fact]
    public void A_stale_socket_file_is_replaced()
    {
        File.WriteAllText(_path, "stale");
        var (server, _, client) = Start();
        using (server)
        using (client)
        {
            var (ok, _) = client.Send("ping");
            Assert.True(ok);
        }
    }

    [Fact]
    public void A_live_server_is_not_hijacked()
    {
        var (server, _, _) = Start();
        using (server)
        {
            // The typed exception is the signal Program acts on (fatal at
            // startup), so the exact type is the contract, not its base.
            Assert.Throws<ControlSocketInUseException>(() => new ControlServer(_path, new FakeTarget()));
        }
    }

    [Fact]
    public void Multiple_requests_one_connection()
    {
        var (server, target, client) = Start();
        using (server)
        using (client)
        {
            for (var i = 0; i < 5; i++)
            {
                var (ok, _) = client.Send("state");
                Assert.True(ok);
            }

            client.Send("warp 5 6");
            Assert.Equal(6, target.Calls.Count);
            Assert.Equal("warp 5 6", target.Calls[^1]);
        }
    }
}
