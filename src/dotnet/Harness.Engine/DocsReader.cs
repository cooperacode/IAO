using System.Text;

namespace Harness.Engine;

/// <summary>
/// Reads a set of documents (`*.md` and `*.txt`) from a folder to inject into the prompt.
/// It's the alternative input to the interactive one: the flow reads existing material
/// (specs, notes, transcripts) and the model synthesizes a brief from it.
///
/// Analogous to how <see cref="PromptFormatter"/> injects skills — the reading is
/// deterministic (done in code), only the synthesis is left to the model.
/// </summary>
public static class DocsReader
{
    // UTF-8 octet ceiling (RFC Appendix B item 1: measure in bytes, not .NET chars, so the
    // ceiling has the same meaning across the .NET/Python/Rust engines): injecting giant
    // docs silently burns tokens, and the repo measures tokens (see bench/). On overflow,
    // truncates at a valid leading-byte boundary and warns on stderr. The field name
    // (DocsMaxChars, from harness.json/HarnessConfig) doesn't change — only the measured unit.
    private static int MaxChars => HarnessConfig.Current.DocsMaxChars;

    private static readonly string[] Extensions = [".md", ".txt"];

    /// <summary>Does the folder exist and hold at least one `*.md`/`*.txt` file?</summary>
    public static bool HasDocs(string folder)
    {
        var dir = PathResolver.Resolve(folder);
        return Directory.Exists(dir) && Files(dir).Length > 0;
    }

    /// <summary>
    /// Concatenates the documents in alphabetical order, each under a
    /// `## &lt;file-name&gt;` heading, and also returns the list of names (to cite the sources).
    /// </summary>
    public static (string Content, string[] Files) Read(string folder)
    {
        var dir = PathResolver.Resolve(folder);
        if (!Directory.Exists(dir))
            return (string.Empty, []);

        var files = Files(dir);
        var names = new List<string>(files.Length);
        var sb = new StringBuilder();

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                HarnessLog.Error($"[DocsReader] failed to read {name}: {ex.Message}");
                continue;
            }

            names.Add(name);
            sb.Append("## ").AppendLine(name).AppendLine().AppendLine(text).AppendLine();

            if (Encoding.UTF8.GetByteCount(sb.ToString()) > MaxChars)
            {
                HarnessLog.Error(
                    $"[DocsReader] content exceeded {MaxChars} bytes (UTF-8); truncating at {name}.");
                var truncated = TruncateUtf8Bytes(sb.ToString(), MaxChars);
                sb.Clear().Append(truncated);
                break;
            }
        }

        return (sb.ToString().TrimEnd(), names.ToArray());
    }

    /// <summary>
    /// Cuts <paramref name="text"/> at no more than <paramref name="maxBytes"/> UTF-8
    /// octets, backing off to a valid leading-byte boundary — never splits a multi-byte
    /// character (accent, emoji) in half, which would produce invalid bytes/replacement
    /// characters.
    /// </summary>
    private static string TruncateUtf8Bytes(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return text;

        var cut = maxBytes;
        // A UTF-8 continuation byte has its two most significant bits as "10" (0x80..0xBF);
        // backing off until a byte that is NOT a continuation guarantees [0, cut) is a
        // complete sequence.
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
            cut--;

        return Encoding.UTF8.GetString(bytes, 0, cut);
    }

    private static string[] Files(string dir) =>
        Directory.EnumerateFiles(dir)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
