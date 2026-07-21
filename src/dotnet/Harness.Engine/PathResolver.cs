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

        var fromCwd = Path.GetFullPath(trimmed, Directory.GetCurrentDirectory());
        if (File.Exists(fromCwd) || Directory.Exists(fromCwd))
            return fromCwd;

        return Path.GetFullPath(trimmed, AppContext.BaseDirectory);
    }
}
