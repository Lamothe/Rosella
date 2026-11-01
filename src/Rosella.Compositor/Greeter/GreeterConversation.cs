namespace Rosella.Compositor.Greeter;

/// <summary>Where one authentication attempt has got to.</summary>
internal enum GreeterPhase
{
    /// <summary>No attempt in flight: the form is editable and Enter starts one.</summary>
    Idle,

    /// <summary>"create_session" was sent; greetd has not asked for anything yet.</summary>
    Created,

    /// <summary>greetd asked a question; the next submission answers it.</summary>
    AwaitingResponse,

    /// <summary>An answer was posted; greetd is checking it.</summary>
    AwaitingResult,

    /// <summary>greetd returned success; start_session may be sent.</summary>
    Ready,
}

/// <summary>What the caller must do after a greetd message.</summary>
internal enum GreeterStepKind
{
    /// <summary>
    /// Informational: show the text, then answer with a response-less
    /// post_auth_message_response (greetd blocks until it gets one) and
    /// keep listening.
    /// </summary>
    Acknowledge,

    /// <summary>Ask the user: the text is the prompt, Secret says how to echo it.</summary>
    Respond,

    /// <summary>The session is authenticated and may be started.</summary>
    Ready,

    /// <summary>Wrong credential: show the text, reset to Idle, allow a retry.</summary>
    Retry,

    /// <summary>A general failure: show the text, reset to Idle, allow a retry.</summary>
    Fatal,
}

/// <summary>One transition's instruction to the caller.</summary>
internal readonly record struct GreeterStep(GreeterStepKind Kind, string Text = "", bool Secret = false);

/// <summary>
/// The greetd conversation as a pure state machine: it consumes parsed
/// messages and returns what to do, without touching a socket, a clock or a
/// screen. The controller owns the I/O; this type owns the sequence, which is
/// what the tests pin.
///
/// greetd is explicitly open-ended: any number of auth messages of any type
/// may arrive. A greeter must not assume the first one is the password.
/// </summary>
internal sealed class GreeterConversation
{
    /// <summary>Text shown when greetd rejects the credential without saying why.</summary>
    internal const string RejectedCredential = "Incorrect PIN or password.";

    internal GreeterPhase Phase { get; private set; } = GreeterPhase.Idle;

    /// <summary>The last prompt's text (empty when greetd has not asked).</summary>
    internal string Prompt { get; private set; } = string.Empty;

    /// <summary>Whether the current prompt asks for a secret (echo off).</summary>
    internal bool PromptIsSecret { get; private set; }

    /// <summary>The last retryable/fatal failure's text, for the error line.</summary>
    internal string Failure { get; private set; } = string.Empty;

    /// <summary>True while a session has been created and not yet started or failed.</summary>
    internal bool InFlight => Phase is GreeterPhase.Created or GreeterPhase.AwaitingResponse or GreeterPhase.AwaitingResult;

    /// <summary>Starts an attempt. The caller then sends create_session.</summary>
    internal void Start(string username)
    {
        if (Phase != GreeterPhase.Idle)
        {
            throw new InvalidOperationException($"a login attempt is already {Phase}");
        }

        Phase = GreeterPhase.Created;
        Prompt = string.Empty;
        PromptIsSecret = false;
        Failure = string.Empty;
    }

    /// <summary>Records that an answer was sent; the next message is its result.</summary>
    internal void Submit()
    {
        if (Phase != GreeterPhase.AwaitingResponse)
        {
            throw new InvalidOperationException($"cannot answer a prompt while {Phase}");
        }

        Phase = GreeterPhase.AwaitingResult;
    }

    /// <summary>
    /// Records that a response-less acknowledgement was sent for an info or
    /// error message. The next message is greetd's next question or verdict.
    /// </summary>
    internal void Acknowledge()
    {
        Phase = GreeterPhase.AwaitingResult;
    }

    /// <summary>The next greetd message. Returns what the caller must do about it.</summary>
    internal GreeterStep OnMessage(GreeterMessage message)
    {
        switch (message)
        {
            case GreeterAuthMessage auth:
                return OnAuthMessage(auth);

            case GreeterSuccess:
                // Success can come straight from create_session (a user with
                // no PAM questions) or after an answer was posted.
                if (Phase is GreeterPhase.Created or GreeterPhase.AwaitingResponse or GreeterPhase.AwaitingResult)
                {
                    Phase = GreeterPhase.Ready;
                    Prompt = string.Empty;
                    return new GreeterStep(GreeterStepKind.Ready);
                }

                Fail(string.Empty);
                return new GreeterStep(GreeterStepKind.Fatal, "greetd reported success unexpectedly.");

            case GreeterErrorMessage error:
                // Rejected credentials are not fatal: the greeter stays up and
                // the next Enter starts a fresh attempt. Everything else is a
                // failure of this attempt only, never of the greeter.
                Fail(error.Description.Length > 0
                    ? error.Description
                    : error.ErrorType == "auth_error" ? RejectedCredential : "greetd could not complete the sign-in.");

                // Deployment note: under greetd this runs as the unprivileged
                // 'greeter' user, while /etc/rosella/pin is 0600 root:root.
                // pam_exec therefore runs rosella-pin as 'greeter', it exits 2
                // without reading the file, and the PAM stack falls through to
                // pam_unix. A correct PIN is rejected until the file is
                // deployed 0640 root:greeter; the password remains the
                // supported degraded path, and this message must not pretend
                // to know which of the two was typed.
                return new GreeterStep(
                    error.ErrorType == "auth_error" ? GreeterStepKind.Retry : GreeterStepKind.Fatal,
                    Failure);

            default:
                Fail(string.Empty);
                return new GreeterStep(GreeterStepKind.Fatal, "greetd sent a message this greeter does not understand.");
        }
    }

    /// <summary>
    /// Abandons the attempt. True when a session was configured and the
    /// caller must send cancel_session to keep greetd's state clean.
    /// </summary>
    internal bool Cancel()
    {
        if (!InFlight)
        {
            return false;
        }

        Phase = GreeterPhase.Idle;
        Prompt = string.Empty;
        PromptIsSecret = false;
        return true;
    }

    /// <summary>
    /// Forces the machine back to Idle from any phase. Used when a
    /// start_session fails after greetd already reported success: the
    /// session is no longer startable, and Ready would make the next
    /// Start() throw.
    /// </summary>
    internal void Abandon()
    {
        Phase = GreeterPhase.Idle;
        Prompt = string.Empty;
        PromptIsSecret = false;
        Failure = string.Empty;
    }

    private GreeterStep OnAuthMessage(GreeterAuthMessage message)
    {
        if (Phase == GreeterPhase.Idle || Phase == GreeterPhase.Ready)
        {
            Fail(string.Empty);
            return new GreeterStep(GreeterStepKind.Fatal, "greetd asked for authentication with no login in progress.");
        }

        switch (message.Type)
        {
            case "info":
            case "error":
                // greetd's conversation blocks until the greeter answers even
                // an informative message; the answer is a response-less
                // post_auth_message_response. An "error" here is a note in
                // the flow (e.g. an expired token), not a failed request;
                // request errors arrive as "error" messages instead.
                return new GreeterStep(GreeterStepKind.Acknowledge, message.Text);

            case "secret":
            case "visible":
                Prompt = message.Text;
                PromptIsSecret = message.Type == "secret";
                Phase = GreeterPhase.AwaitingResponse;
                return new GreeterStep(GreeterStepKind.Respond, message.Text, PromptIsSecret);

            default:
                Fail(string.Empty);
                return new GreeterStep(
                    GreeterStepKind.Fatal,
                    $"greetd asked for an unsupported authentication message type '{message.Type}'.");
        }
    }

    private void Fail(string failure)
    {
        Failure = failure;
        Phase = GreeterPhase.Idle;
        Prompt = string.Empty;
        PromptIsSecret = false;
    }
}