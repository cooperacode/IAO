namespace Harness.Engine;

/// <summary>
/// Output template for an artifact: <c>.harness/skills/&lt;name&gt;/ARTIFACT.md</c> with
/// <c>{{key}}</c> placeholders replaced by values from <see cref="StateStore"/>. The
/// artifact's markdown shape lives alongside the skill that produces it — outside C#,
/// editable without recompiling. Pure string substitution: deterministic, zero token, and
/// AOT-safe.
/// </summary>
public static class ArtifactTemplate
{
    /// <summary>Reads the skill's template; <c>null</c> if the skill doesn't define one (the caller decides the fallback).</summary>
    public static string? Load(string skillName)
    {
        try
        {
            var path = PathResolver.Resolve(Path.Combine(".harness", "skills", skillName, "ARTIFACT.md"));
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            HarnessLog.Error($"[ArtifactTemplate] failed to read {skillName}'s template: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Replaces each <c>{{key}}</c> with its corresponding value. Placeholders with no
    /// value remain in the text — a visible sign of missing data, not a silent error.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = template;

        foreach (var (key, value) in values)
            result = result.Replace("{{" + key + "}}", value);

        return result;
    }
}
