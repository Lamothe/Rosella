using System.Collections.Concurrent;
using Rosella.Input;

namespace Rosella.Compositor.Greeter;

/// <summary>
/// Drives the greetd conversation on its own thread while the input threads
/// feed the form. The division is deliberate: the form's state is
/// <see cref="GreeterState"/>, the protocol's sequence is
/// <see cref="GreeterConversation"/>, and this class is the only place that
/// owns the socket and the waiting between the two.
///
/// Authentication failures never end the process. Only greetd accepting a
/// session (the greeter must exit for that session to start) or the socket
/// itself dying does.
/// </summary>
internal sealed class GreeterController : IDisposable
{
    private readonly record struct GreeterCommand(bool Cancel, bool AnswersPrompt, string Username, string Secret)
    {
        internal static GreeterCommand CancelSignIn => new(true, false, string.Empty, string.Empty);

        internal static GreeterCommand Submit(string username, string secret, bool answersPrompt) =>
            new(false, answersPrompt, username, secret);
    }

    private readonly GreeterState _state;
    private readonly Stream _stream;
    private readonly IDisposable? _transport;
    private readonly GreeterFrameReader _reader;
    private readonly GreeterConversation _conversation = new();
    private readonly Action _quit;
    private readonly BlockingCollection<GreeterCommand> _commands = new();
    private readonly Thread _driver;
    private volatile bool _disposed;
    private volatile bool _shift;
    private bool _started;
    private int _disposeState;
    private volatile int _exitCode;

    internal GreeterController(GreeterState state, GreetdConnection connection, Action quit)
        : this(state, connection.Stream, connection, quit)
    {
    }

    /// <summary>The transport seam: tests script greetd's side without a socket.</summary>
    internal GreeterController(GreeterState state, Stream stream, IDisposable? transport, Action quit)
    {
        _state = state;
        _stream = stream;
        _transport = transport;
        _reader = new GreeterFrameReader(stream);
        _quit = quit;
        _driver = new Thread(Drive)
        {
            IsBackground = true,
            Name = "rosella-greeter-driver",
        };
    }

    /// <summary>0 when a session was handed over cleanly, 1 when greetd disappeared.</summary>
    internal int ExitCode => _exitCode;

    internal void Start()
    {
        _started = true;
        _driver.Start();
    }

    /// <summary>One evdev key event, called from the input reader thread.</summary>
    internal void Key(ushort code, int value)
    {
        if (code is InputConstants.KeyLeftShift or InputConstants.KeyRightShift)
        {
            _shift = value != InputConstants.KeyReleased;
            return;
        }

        if (value == InputConstants.KeyReleased)
        {
            return;
        }

        var key = GreeterKeys.Map(code, _shift);
        switch (key.Action)
        {
            case GreeterKeyAction.Character:
                _state.Type(key.Character);
                break;

            case GreeterKeyAction.Backspace:
                _state.Backspace();
                break;

            case GreeterKeyAction.Tab when value == InputConstants.KeyPressed:
                _state.SwitchFocus();
                break;

            case GreeterKeyAction.Enter when value == InputConstants.KeyPressed:
                Submit();
                break;

            case GreeterKeyAction.Escape when value == InputConstants.KeyPressed:
                Escape();
                break;

            case GreeterKeyAction.Up when value == InputConstants.KeyPressed:
                _state.MoveSession(-1);
                break;

            case GreeterKeyAction.Down when value == InputConstants.KeyPressed:
                _state.MoveSession(1);
                break;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _disposed = true;
        _commands.CompleteAdding();
        _transport?.Dispose();

        // A controller whose Start() was never reached (a Scanout or signal
        // registration failed) still unwinds through here; Join on an
        // unstarted thread would replace the real exception with a
        // ThreadStateException.
        if (_started && _driver.ThreadState != ThreadState.Unstarted)
        {
            _driver.Join(1000);
        }

        _commands.Dispose();
    }

    private void Submit()
    {
        if (!_state.TrySubmit(out var username, out var secret, out var answersPrompt))
        {
            return;
        }

        Enqueue(GreeterCommand.Submit(username, secret, answersPrompt));
    }

    private void Escape()
    {
        // First Esc empties the field; a second, with nothing left to clear,
        // abandons an open prompt (cancel_session) and reopens the form.
        if (_state.CancelPending)
        {
            Enqueue(GreeterCommand.CancelSignIn);
            return;
        }

        _state.ClearFocused();
    }

    private void Enqueue(GreeterCommand command)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _commands.Add(command);
        }
        catch (InvalidOperationException)
        {
            // Disposed between the check and the add: the driver is gone.
        }
    }

    private void Drive()
    {
        while (!_disposed)
        {
            GreeterCommand command;
            try
            {
                command = _commands.Take();
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                return;
            }

            if (command.Cancel)
            {
                CancelAttempt();
                continue;
            }

            // An answer typed for a prompt that was then cancelled must not
            // become the password of an unattended new attempt.
            if (command.AnswersPrompt)
            {
                continue;
            }

            try
            {
                if (Attempt(command))
                {
                    return; // the session was handed over; the process must exit
                }
            }
            catch (InvalidOperationException exception)
            {
                // A state-machine violation must not kill the greeter process.
                Console.Error.WriteLine($"rosella: greeter: {exception.Message}");
                _conversation.Abandon();
                CancelConfiguredSession();
                _state.Fail("The sign-in conversation failed. Try again.");
            }
        }
    }

    /// <summary>One login attempt. True means the caller must quit the greeter.</summary>
    private bool Attempt(GreeterCommand command)
    {
        _conversation.Start(command.Username);
        if (!Send(GreeterRequests.CreateSession(command.Username)))
        {
            return true;
        }

        // The secret was typed before the prompt arrived: it is the answer to
        // the first secret/visible question, and later questions wait for
        // their own Enter.
        string? pendingResponse = command.Secret;
        while (!_disposed)
        {
            if (!TryRead(out var message))
            {
                return true;
            }

            var step = _conversation.OnMessage(message);
            switch (step.Kind)
            {
                case GreeterStepKind.Acknowledge:
                    // greetd waits for an answer even to an info/error note.
                    _state.SetStatus(step.Text);
                    _conversation.Acknowledge();
                    if (!Send(GreeterRequests.PostAuthMessageResponse(null)))
                    {
                        return true;
                    }

                    break;

                case GreeterStepKind.Respond:
                {
                    _state.SetPrompt(step.Text, step.Secret);
                    var response = pendingResponse;
                    pendingResponse = null;
                    if (response is null)
                    {
                        response = WaitForAnswer();
                        if (response is null)
                        {
                            return false; // cancelled: the form is open again
                        }
                    }

                    _conversation.Submit();
                    _state.ResponseSent();
                    if (!Send(GreeterRequests.PostAuthMessageResponse(response)))
                    {
                        return true;
                    }

                    break;
                }

                case GreeterStepKind.Ready:
                    return StartSession();

                case GreeterStepKind.Retry:
                case GreeterStepKind.Fatal:
                    // A failed authentication leaves greetd's session under
                    // configuration in place; without cancel_session the next
                    // create_session is rejected with "a session is already
                    // being configured" and every retry fails the same way.
                    _state.Fail(step.Text);
                    CancelConfiguredSession();
                    return false;
            }
        }

        return false;
    }

    /// <summary>Waits for the answer to an open prompt. Null means cancelled.</summary>
    private string? WaitForAnswer()
    {
        while (!_disposed)
        {
            GreeterCommand answer;
            try
            {
                answer = _commands.Take();
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                return null;
            }

            if (answer.Cancel)
            {
                CancelAttempt();
                return null;
            }

            if (!answer.AnswersPrompt)
            {
                // Not an answer to this prompt (a stale new-attempt command);
                // keep waiting rather than spending it as the secret.
                continue;
            }

            return answer.Secret;
        }

        return null;
    }

    /// <summary>
    /// Abandons the session being configured. greetd keeps per-connection
    /// state, so the cancel is sent whenever an attempt might have created a
    /// session; a second Esc with nothing in flight costs one harmless
    /// request.
    /// </summary>
    private void CancelAttempt()
    {
        _conversation.Cancel();
        CancelConfiguredSession();
        _state.Cancel();
    }

    /// <summary>
    /// Sends cancel_session and consumes its reply. Every greetd request is
    /// answered, and a stale reply left on the socket would be read as the
    /// answer to the next create_session — a stale success would start a
    /// session that was never authenticated.
    /// </summary>
    private bool CancelConfiguredSession()
    {
        if (!Send(GreeterRequests.CancelSession()))
        {
            return false;
        }

        if (!TryRead(out var reply))
        {
            return false;
        }

        if (reply is GreeterErrorMessage error)
        {
            // The reply is consumed either way; a failure to cancel is worth
            // a log line, not the end of the greeter.
            Console.Error.WriteLine($"rosella: greeter: cancel_session failed: {error.Description}");
        }

        return true;
    }

    private bool StartSession()
    {
        if (_state.SelectedSession is not { } session)
        {
            _state.Fail($"No sessions found in {GreeterSessions.DefaultDirectory}.");
            return false;
        }

        _state.Starting(session.Name);
        if (!Send(GreeterRequests.StartSession(session.Command, session.Environment)))
        {
            return true;
        }

        // greetd replies before the session can start, and it starts the
        // session only after this process terminates. Reading the reply keeps
        // the request/response stream aligned: an error (e.g. "session is not
        // ready") reopens the form instead of exiting and making greetd
        // recover from a greeter that died without a session.
        if (!TryRead(out var reply))
        {
            return true;
        }

        if (reply is GreeterErrorMessage error)
        {
            _conversation.Abandon();
            _state.Fail($"greetd could not start {session.Name}: {error.Description}");
            CancelConfiguredSession();
            return false;
        }

        if (reply is not GreeterSuccess)
        {
            _conversation.Abandon();
            _state.Fail("greetd sent an unexpected reply to start_session.");
            CancelConfiguredSession();
            return false;
        }

        // The environment carries XDG_SESSION_TYPE and the desktop identity
        // because greetd sets neither and logind reads the type before the
        // session's own scripts run.
        _quit();
        return true;
    }

    private bool TryRead(out GreeterMessage message)
    {
        message = null!;
        try
        {
            var json = _reader.Read();
            if (GreeterMessage.Parse(json) is not { } parsed)
            {
                Lost("greetd sent a message this greeter does not understand.");
                return false;
            }

            message = parsed;
            return true;
        }
        catch (Exception exception) when (exception is IOException or GreeterProtocolException or ObjectDisposedException)
        {
            Lost($"Lost the connection to greetd: {exception.Message}");
            return false;
        }
    }

    private bool Send(string json)
    {
        try
        {
            GreeterFraming.Write(_stream, json);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Lost($"Lost the connection to greetd: {exception.Message}");
            return false;
        }
    }

    private void Lost(string message)
    {
        if (_disposed)
        {
            return;
        }

        _exitCode = 1;
        _state.Fail(message);
        _quit();
    }
}