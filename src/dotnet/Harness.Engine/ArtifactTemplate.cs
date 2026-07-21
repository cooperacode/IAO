namespace Harness.Engine;

/// <summary>
/// Template de saída de um artefato: <c>skills/&lt;name&gt;/ARTIFACT.md</c> com placeholders
/// <c>{{chave}}</c> substituídos por valores do <see cref="StateStore"/>. A forma markdown
/// do artefato mora junto da skill que o produz — fora do C#, editável sem recompilar.
/// Substituição pura de strings: determinística, zero token e AOT-safe.
/// </summary>
public static class ArtifactTemplate
{
    /// <summary>Lê o template da skill; <c>null</c> se a skill não define um (o caller decide o fallback).</summary>
    public static string? Load(string skillName)
    {
        try
        {
            var path = PathResolver.Resolve(Path.Combine("skills", skillName, "ARTIFACT.md"));
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ArtifactTemplate] falha ao ler template de {skillName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Substitui cada <c>{{chave}}</c> pelo valor correspondente. Placeholders sem valor
    /// permanecem no texto — sinal visível de dado faltante, não erro silencioso.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = template;

        foreach (var (key, value) in values)
            result = result.Replace("{{" + key + "}}", value);

        return result;
    }
}
