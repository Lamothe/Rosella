using System.Text.Json;

namespace Rosella.Compositor.Greeter;

/// <summary>
/// One message from greetd. The socket carries JSON objects with a "type"
/// discriminator; anything else is not a greetd message and is rejected at
/// the boundary instead of leaking into the conversation.
/// </summary>
internal abstract record GreeterMessage
{
    /// <summary>Parses one frame's JSON. Null when it is not a known greetd message.</summary>
    internal static GreeterMessage? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return GetString(root, "type") switch
            {
                "success" => new GreeterSuccess(),
                "auth_message" => new GreeterAuthMessage(
                    GetString(root, "auth_message_type") ?? string.Empty,
                    GetString(root, "auth_message") ?? string.Empty),
                "error" => new GreeterErrorMessage(
                    GetString(root, "error_type") ?? "error",
                    GetString(root, "description") ?? string.Empty),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>The request succeeded; for create_session/post_auth_message_response the session is ready.</summary>
internal sealed record GreeterSuccess : GreeterMessage;

/// <summary>
/// An authentication prompt (secret or visible), or an informational/error
/// message that needs no answer. <see cref="Type"/> is the raw enum string
/// so an unknown future value is visible in the failure message.
/// </summary>
internal sealed record GreeterAuthMessage(string Type, string Text) : GreeterMessage;

/// <summary>
/// A failed request. "auth_error" is retryable (wrong credential);
/// anything else is a general error for this attempt.
/// </summary>
internal sealed record GreeterErrorMessage(string ErrorType, string Description) : GreeterMessage;

/// <summary>The JSON bodies greetd expects, built in one place so the field names cannot drift.</summary>
internal static class GreeterRequests
{
    internal static string CreateSession(string username) =>
        JsonSerializer.Serialize(new { type = "create_session", username });

    internal static string PostAuthMessageResponse(string? response) =>
        response is null
            // greetd-ipc(7): a response "does not need to be set" for an
            // informative message. greetd's conversation accepts None and
            // rejects Some(""), so the field is omitted, not sent empty.
            ? JsonSerializer.Serialize(new { type = "post_auth_message_response" })
            : JsonSerializer.Serialize(new { type = "post_auth_message_response", response });

    internal static string StartSession(IReadOnlyList<string> command, IReadOnlyList<string> environment) =>
        JsonSerializer.Serialize(new { type = "start_session", cmd = command, env = environment });

    internal static string CancelSession() =>
        JsonSerializer.Serialize(new { type = "cancel_session" });
}