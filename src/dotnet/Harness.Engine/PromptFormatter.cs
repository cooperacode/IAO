using System.Text;

namespace Harness.Engine;

/// <summary>Monta o bloco de instrução (input/response/skills) entregue ao modelo.</summary>
public static class PromptFormatter
{
    public static Dictionary<string, string> Skills(params string[] names)
    {
        var skills = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
            skills[name] = Path.Combine("skills", name, "SKILL.md");

        return skills;
    }

    public static string Format(string input, Envelope output, IDictionary<string, string>? skills = null)
    {
        // Reinjeta o contexto do driver (capturado no `start`, ver TaskRegistry/StateStore)
        // em toda saída — ponto único, para que nenhuma task precise repassá-lo manualmente.
        var enriched = output.Context is null
            ? output with { Context = StateStore.GetContext() }
            : output;

        return $"""
        Execute the instruction inside the `input` tag. Then reply with the result as JSON.

        Output contract — a reply that breaks any of these rules is invalid and wastes a retry:
        1. Output EXACTLY one JSON object, on a SINGLE line, matching the shape in the `response` tag with the placeholders replaced by real values.
        2. The object is the ONLY thing you output: no markdown code fences, no comments, no prose before or after it, nothing.
        3. Keep the same keys, types and nesting as the schema — do not add, remove, rename fields, or wrap the object in an array.
        4. Every value must be valid JSON: use only double quotes for strings, escape `"` and `\` inside them, and replace any line break inside a value with the literal characters `\n` — never a raw newline. No trailing commas.
        5. Before answering, mentally re-parse your own output as JSON; if it would fail to parse, fix it before sending.

        {ReadSkills(skills)}
        <input>
            {input}
        </input>
        <response>
            {enriched.ToJson()}
        </response>
        """;
    }

    private static string ReadSkills(IDictionary<string, string>? skills)
    {
        if (skills is null)
            return string.Empty;

        var sb = new StringBuilder();

        foreach (var skill in skills)
        {
            if (string.IsNullOrWhiteSpace(skill.Value))
                continue;

            var path = PathResolver.Resolve(skill.Value);
            if (!File.Exists(path))
                continue;

            var content = File.ReadAllText(path);
            // Inline the content but preserve line breaks as literal "\n" markers
            content = content.Replace("\r\n", "\\n").Replace("\n", "\\n");

            sb.Append($"""<skill id="{skill.Key}">{content}</skill>""");
        }

        return sb.Length == 0
            ? string.Empty
            : $"""
            <skills>
                {sb}
            </skills>
            """;
    }
}
