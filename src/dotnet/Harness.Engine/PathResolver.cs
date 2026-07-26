namespace Harness.Engine;

/// <summary>
/// Resolve caminhos relativos ao diretório de trabalho (raiz do repo, de onde o driver
/// invoca o harness), com fallback para o diretório do binário. Compartilhado por quem
/// injeta arquivos no prompt (skills, docs).
/// </summary>
public static class PathResolver
{
    public static string Resolve(string path)
    {
        var trimmed = path.Trim();
        if (Path.IsPathRooted(trimmed))
            return trimmed;

        var cwd = Directory.GetCurrentDirectory();
        var fromCwd = Path.GetFullPath(trimmed, cwd);
        if (Exists(fromCwd) && IsContained(fromCwd, cwd))
            return fromCwd;

        var baseDir = AppContext.BaseDirectory;
        var fromBase = Path.GetFullPath(trimmed, baseDir);
        if (Exists(fromBase) && IsContained(fromBase, baseDir))
            return fromBase;

        // Nem o CWD nem o BaseDirectory serviram — ausente, ou um symlink desviando o alvo
        // para fora das duas bases autorizadas. Devolve o caminho join original SEM seguir o
        // link (não o alvo resolvido, que estaria fora da base); o File.Exists subsequente do
        // chamador falha naturalmente quando o alvo de fato não é acessível/autorizado.
        return fromBase;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Containment por symlink (RFC §6.3): resolve o alvo final do link (se <paramref name="candidate"/>
    /// for um) e confere que ele está de fato dentro de <paramref name="baseDir"/>, comparando
    /// caminhos canônicos por prefixo de diretório real — não prefixo de string lexical (o que
    /// deixaria "/base-evil" passar como contido em "/base").
    /// </summary>
    private static bool IsContained(string candidate, string baseDir)
    {
        var normalizedBase = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(ResolveFinalTarget(candidate))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (target.Equals(normalizedBase, comparison))
            return true;

        var baseWithSep = normalizedBase + Path.DirectorySeparatorChar;
        return target.StartsWith(baseWithSep, comparison);
    }

    /// <summary>Segue o link (se houver) até o alvo final; devolve o próprio caminho se não for um link ou a resolução falhar (ex.: link quebrado).</summary>
    private static string ResolveFinalTarget(string path)
    {
        try
        {
            if (File.Exists(path))
                return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;

            if (Directory.Exists(path))
                return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch
        {
            // Link quebrado/inacessível: trata como não resolvido — cai no path original,
            // e a checagem de containment acima decide com base nele.
        }

        return path;
    }
}
