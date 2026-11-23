namespace Rosella.Pin;

/// <summary>
/// Reading the secret. Interactive terminals get a silent prompt; anything
/// else (pam_exec pipes the authtok to stdin) is read as one line and never
/// prompted for, so the PAM path writes nothing to stdout.
/// </summary>
internal static class Prompt
{
    private const int MaxLength = 128;

    /// <summary>Null when the user interrupted, so the caller can exit non-zero.</summary>
    internal static string? ReadSecret(string label)
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }

        Console.Error.Write(label);
        var buffer = new char[MaxLength];
        var length = 0;
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.Error.WriteLine();
                    return new string(buffer, 0, length);
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (length > 0)
                    {
                        length--;
                        Console.Error.Write("\b \b");
                    }

                    continue;
                }

                if (key.Key == ConsoleKey.Escape
                    || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
                {
                    Console.Error.WriteLine();
                    return null;
                }

                if (char.IsControl(key.KeyChar) || length == MaxLength)
                {
                    continue;
                }

                buffer[length++] = key.KeyChar;
                Console.Error.Write('*');
            }
        }
        finally
        {
            Array.Clear(buffer);
        }
    }
}