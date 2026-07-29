namespace Harness.Engine;

/// <summary>
/// Resolves paths relative to the working directory (the repo root, from where the driver
/// invokes the harness), with a fallback to the binary's directory. Shared by whatever
/// injects files into the prompt (skills, docs).
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

        // Neither the CWD nor the BaseDirectory worked — missing, or a symlink steering the
        // target outside both authorized bases. Returns the original joined path WITHOUT
        // following the link (not the resolved target, which would be outside the base);
        // the caller's subsequent File.Exists naturally fails when the target isn't
        // actually accessible/authorized.
        return fromBase;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Symlink containment (RFC §6.3): resolves the link's final target (if <paramref name="candidate"/>
    /// is one) and checks it's actually inside <paramref name="baseDir"/>, comparing
    /// canonical paths by real directory prefix — not lexical string prefix (which would
    /// let "/base-evil" pass as contained in "/base").
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

    /// <summary>Follows the link (if any) to its final target; returns the path itself if it's not a link or resolution fails (e.g. a broken link).</summary>
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
            // Broken/inaccessible link: treated as unresolved — falls back to the original
            // path, and the containment check above decides based on it.
        }

        return path;
    }
}
