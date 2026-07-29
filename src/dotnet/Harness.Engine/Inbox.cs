namespace Harness.Engine;

/// <summary>
/// File-based input channel — an alternative to argv for the turn's envelope.
///
/// Single-quoted argument transport (<c>./run-refinement.sh '&lt;JSON&gt;'</c>) has a
/// structural flaw: if the LLM driver forgets the closing quote, the shell enters
/// continuation mode and hangs BEFORE the binary runs — no engine validation can catch
/// it. The inbox takes the payload out of the shell's quoting syntax: the agent writes
/// the JSON here with its file-write tool (never goes through a shell) and runs the
/// script with NO arguments, a bare command that has no way of being left unterminated.
/// </summary>
public static class Inbox
{
    private const string Dir = ".harness";
    public const string Path = ".harness/inbox.json";

    // Trail of the last consumed envelope — avoids reprocessing a stale JSON if the script
    // runs twice without a rewrite, and doubles as a diagnostic.
    public const string ConsumedPath = ".harness/inbox.consumed.json";

    /// <summary>Raw inbox content, or <c>""</c> if it doesn't exist. Parsing/sanitization lives in <see cref="Envelope"/>.</summary>
    public static string Read()
    {
        try
        {
            if (File.Exists(Path))
                return File.ReadAllText(Path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Inbox] failed to read {Path}: {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>Moves the consumed inbox to <see cref="ConsumedPath"/> after a successful parse.</summary>
    public static void Consume()
    {
        try
        {
            if (File.Exists(Path))
            {
                Directory.CreateDirectory(Dir);
                File.Move(Path, ConsumedPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Inbox] failed to consume {Path}: {ex.Message}");
        }
    }
}
