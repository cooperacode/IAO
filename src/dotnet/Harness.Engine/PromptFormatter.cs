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
        Execute the instruction inside the `input` tag. Then produce your reply as a SINGLE line of raw JSON matching the schema in the `response` tag, with the placeholders replaced by real values. Reply with the JSON ONLY: no markdown code fences, no comments, no text before or after it. {ReadSkills(skills)}
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

            sb.Append($"""
                <skill id="{skill.Key}">
                    {content}
                </skill>
            """);
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
