using System.Text;

namespace Rosella.Compositor.Greeter;

/// <summary>Which field typing edits.</summary>
internal enum GreeterField
{
    Username,
    Secret,
}

/// <summary>One render's view of the form, taken atomically.</summary>
internal sealed record GreeterSnapshot(
    string Username,
    string Secret,
    string Prompt,
    string Message,
    bool MessageIsError,
    bool Busy,
    bool AwaitingInput,
    bool SecretPrompt,
    GreeterField Focus,
    string SessionName,
    int SessionCount);

/// <summary>
/// The login form's state: what is typed, which field has focus, the current
/// prompt and error text, and the picked session. Input threads and the greetd
/// driver both mutate it, the render loop only snapshots it, so every
/// transition happens under one lock and the renderer never sees a half-typed
/// password.
/// </summary>
internal sealed class GreeterState
{
    internal const int MaxUsernameLength = 32;

    internal const int MaxSecretLength = 128;

    private readonly object _gate = new();
    private readonly IReadOnlyList<GreeterSession> _sessions;
    private readonly StringBuilder _secret = new();
    private string _username = string.Empty;
    private string _prompt = string.Empty;
    private string _message = string.Empty;
    private bool _messageIsError;
    private bool _busy;
    private bool _awaitingInput;
    private bool _secretPrompt = true;
    private GreeterField _focus = GreeterField.Username;
    private int _sessionIndex;

    internal GreeterState(IReadOnlyList<GreeterSession> sessions, string? defaultUsername = null)
    {
        _sessions = sessions;
        _sessionIndex = GreeterSessions.PreferredIndex(sessions);

        // A known account opens on the secret field, so the PIN is the first
        // thing typed. The username stays editable: Tab reaches it, Esc on it
        // empties it.
        if (defaultUsername is not null && GreeterUser.IsValidName(defaultUsername))
        {
            _username = defaultUsername;
            _focus = GreeterField.Secret;
        }

        if (sessions.Count == 0)
        {
            _message = $"No sessions found in {GreeterSessions.DefaultDirectory}.";
            _messageIsError = true;
        }
    }

    /// <summary>Called after every visible change; the render loop wakes on it.</summary>
    internal Action? Dirty { get; set; }

    /// <summary>The session the picker has selected, or null when the list is empty.</summary>
    internal GreeterSession? SelectedSession
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count == 0 ? null : _sessions[_sessionIndex];
            }
        }
    }

    /// <summary>Appends one character to the focused field. False when nothing changed.</summary>
    internal bool Type(char character)
    {
        var changed = false;
        lock (_gate)
        {
            // While greetd is checking an answer the form is locked; it opens
            // again only when a new prompt needs an answer.
            if ((_busy && !_awaitingInput) || char.IsControl(character))
            {
                return false;
            }

            if (_focus == GreeterField.Username)
            {
                if (_busy || _username.Length >= MaxUsernameLength || character is ' ' or ':' or '/')
                {
                    return false;
                }

                _username += character;
                changed = true;
            }
            else if (_secret.Length < MaxSecretLength)
            {
                _secret.Append(character);
                changed = true;
            }
        }

        if (changed)
        {
            Dirty?.Invoke();
        }

        return changed;
    }

    /// <summary>Removes the last character of the focused field.</summary>
    internal bool Backspace()
    {
        var changed = false;
        lock (_gate)
        {
            if (_busy && !_awaitingInput)
            {
                return false;
            }

            if (_focus == GreeterField.Username)
            {
                if (_username.Length > 0)
                {
                    _username = _username[..^1];
                    changed = true;
                }
            }
            else if (_secret.Length > 0)
            {
                _secret.Length--;
                changed = true;
            }
        }

        if (changed)
        {
            Dirty?.Invoke();
        }

        return changed;
    }

    /// <summary>Esc's first job: empty the field being typed in.</summary>
    internal bool ClearFocused()
    {
        var changed = false;
        lock (_gate)
        {
            if (_busy && !_awaitingInput)
            {
                return false;
            }

            if (_focus == GreeterField.Username)
            {
                if (_username.Length > 0)
                {
                    _username = string.Empty;
                    changed = true;
                }
            }
            else if (_secret.Length > 0)
            {
                _secret.Clear();
                changed = true;
            }
        }

        if (changed)
        {
            Dirty?.Invoke();
        }

        return changed;
    }

    /// <summary>Tab: username or secret, never while an answer is in flight.</summary>
    internal bool SwitchFocus()
    {
        lock (_gate)
        {
            if (_busy)
            {
                return false;
            }

            _focus = _focus == GreeterField.Username ? GreeterField.Secret : GreeterField.Username;
        }

        Dirty?.Invoke();
        return true;
    }

    /// <summary>Up/Down on the session picker. Wraps; ignored while signing in.</summary>
    internal bool MoveSession(int delta)
    {
        lock (_gate)
        {
            if (_busy || _sessions.Count < 2)
            {
                return false;
            }

            _sessionIndex = ((_sessionIndex + delta) % _sessions.Count + _sessions.Count) % _sessions.Count;
        }

        Dirty?.Invoke();
        return true;
    }

    /// <summary>
    /// Enter. For a fresh attempt this validates the form and clears the
    /// secret; while a prompt is open it answers that prompt. False means
    /// "nothing to submit" (busy, or a validation message was shown).
    /// </summary>
    internal bool TrySubmit(out string username, out string secret, out bool answersPrompt)
    {
        username = string.Empty;
        secret = string.Empty;
        answersPrompt = false;
        var accepted = false;
        var changed = false;
        lock (_gate)
        {
            if (_busy && !_awaitingInput)
            {
                return false;
            }

            answersPrompt = _busy && _awaitingInput;
            if (!_busy && _username.Length == 0)
            {
                _message = "Enter your username.";
                _messageIsError = true;
                _focus = GreeterField.Username;
                changed = true;
            }
            else if (_sessions.Count == 0)
            {
                _message = $"No sessions found in {GreeterSessions.DefaultDirectory}.";
                _messageIsError = true;
                changed = true;
            }
            else
            {
                username = _username;
                secret = _secret.ToString();
                _secret.Clear();
                _busy = true;
                _awaitingInput = false;
                _prompt = string.Empty;
                _secretPrompt = true;
                _message = string.Empty;
                _messageIsError = false;
                _focus = GreeterField.Secret;
                accepted = true;
                changed = true;
            }
        }

        if (changed)
        {
            Dirty?.Invoke();
        }

        return accepted;
    }

    /// <summary>Whether Esc should abandon the attempt rather than clear the field.</summary>
    internal bool CancelPending
    {
        get
        {
            lock (_gate)
            {
                return _busy && _awaitingInput && _secret.Length == 0;
            }
        }
    }

    /// <summary>greetd asked a question; the field is editable again.</summary>
    internal void SetPrompt(string prompt, bool secretPrompt)
    {
        lock (_gate)
        {
            _busy = true;
            _awaitingInput = true;
            _prompt = prompt.Length > 0 ? prompt : secretPrompt ? "Password" : "Response";
            _secretPrompt = secretPrompt;
            _message = string.Empty;
            _messageIsError = false;
            _focus = GreeterField.Secret;
            _secret.Clear();
        }

        Dirty?.Invoke();
    }

    /// <summary>An informational auth message: show it, keep waiting.</summary>
    internal void SetStatus(string message)
    {
        lock (_gate)
        {
            _message = message;
            _messageIsError = false;
        }

        Dirty?.Invoke();
    }

    /// <summary>The answer was posted; the form waits for greetd's verdict.</summary>
    internal void ResponseSent()
    {
        lock (_gate)
        {
            _awaitingInput = false;
            _prompt = string.Empty;
            _secretPrompt = true;
        }

        Dirty?.Invoke();
    }

    /// <summary>A failed attempt: reopen the form with the reason on the error line.</summary>
    internal void Fail(string message)
    {
        lock (_gate)
        {
            _busy = false;
            _awaitingInput = false;
            _prompt = string.Empty;
            _secretPrompt = true;
            _message = message;
            _messageIsError = true;
            _focus = GreeterField.Secret;
            _secret.Clear();
        }

        Dirty?.Invoke();
    }

    /// <summary>Esc abandoned an in-flight attempt; the form is editable again.</summary>
    internal void Cancel()
    {
        lock (_gate)
        {
            _busy = false;
            _awaitingInput = false;
            _prompt = string.Empty;
            _secretPrompt = true;
            _message = "Sign-in cancelled.";
            _messageIsError = false;
            _secret.Clear();
        }

        Dirty?.Invoke();
    }

    /// <summary>greetd accepted the login; the chosen session is about to run.</summary>
    internal void Starting(string sessionName)
    {
        lock (_gate)
        {
            _busy = true;
            _awaitingInput = false;
            _prompt = string.Empty;
            _secretPrompt = true;
            _message = $"Starting {sessionName}…";
            _messageIsError = false;
        }

        Dirty?.Invoke();
    }

    /// <summary>A consistent view for one frame.</summary>
    internal GreeterSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new GreeterSnapshot(
                _username,
                _secret.ToString(),
                _prompt,
                _message,
                _messageIsError,
                _busy,
                _awaitingInput,
                _secretPrompt,
                _focus,
                _sessions.Count == 0 ? "No sessions" : _sessions[_sessionIndex].Name,
                _sessions.Count);
        }
    }
}