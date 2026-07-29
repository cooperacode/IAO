namespace Harness.Engine;

/// <summary>
/// Atomic "final" write for the stores under <c>.harness</c>: writes to a temp file in
/// the SAME directory as the destination and swaps it in via
/// <see cref="File.Move(string, string, bool)"/> with <c>overwrite: true</c> — atomic on
/// the same partition since .NET Core 3.0+. Prevents a crash/kill mid-write from leaving
/// the final file truncated or partially overwritten; a concurrent reader always sees
/// either the complete previous version or the complete new one, never an intermediate
/// state. Doesn't apply to the log/trace <c>File.AppendAllText</c> calls — those are
/// already atomic at the event level (one line, one call) and don't need a file swap.
/// </summary>
internal static class AtomicIO
{
    public static void WriteAllTextAtomic(string path, string content)
    {
        var tmp = TempPathFor(path);
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            CleanupBestEffort(tmp);
            throw;
        }
    }

    /// <summary>Same atomic guarantee as <see cref="WriteAllTextAtomic"/>, but copying from an existing source file (e.g. snapshotting a live store into its frozen one).</summary>
    public static void CopyAtomic(string sourcePath, string destinationPath)
    {
        var tmp = TempPathFor(destinationPath);
        try
        {
            File.Copy(sourcePath, tmp, overwrite: true);
            File.Move(tmp, destinationPath, overwrite: true);
        }
        catch
        {
            CleanupBestEffort(tmp);
            throw;
        }
    }

    // Unique name per write in the SAME directory as the destination — Path.GetTempFileName()
    // won't do because it creates outside that folder, breaking the atomic-rename guarantee
    // (same partition).
    private static string TempPathFor(string destination) => $"{destination}.tmp-{Guid.NewGuid():N}";

    private static void CleanupBestEffort(string tmp)
    {
        try
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
        catch
        {
            // Cleanup is best-effort — doesn't mask the original exception already being rethrown.
        }
    }
}
