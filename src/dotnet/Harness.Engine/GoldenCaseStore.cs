using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Loads the golden-set cases from disk. Lives in the engine because AOT-safe
/// deserialization depends on <see cref="HarnessJsonContext"/>, internal to the assembly.
/// </summary>
public static class GoldenCaseStore
{
    public static GoldenCase? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, HarnessJsonContext.Default.GoldenCase);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GoldenCaseStore] failed to load {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Loads every <c>*.json</c> in a directory, ordered by name, skipping invalid ones.</summary>
    public static IReadOnlyList<GoldenCase> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Load)
            .OfType<GoldenCase>()
            .ToList();
    }
}
