using System.Text;
using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Persists each flow artifact to its own file (<c>.harness/&lt;name&gt;.md</c>) and keeps
/// a manifest (<c>.harness/artifacts.json</c>) with the write order. The manifest is the
/// contract between producer and consumer: evaluation reads the artifacts through it,
/// without depending on a combined report.
///
/// Only the PRODUCING flow resets the manifest (in its own <c>start</c>) — the consumer
/// (evaluation) doesn't touch it, for the same reason as the <see cref="Trace"/>/<see cref="StateStore"/>
/// snapshots: the evaluator's start can't erase the evidence it's about to read itself.
/// </summary>
public static class ArtifactStore
{
    private const string Dir = ".harness";
    public const string ManifestPath = ".harness/artifacts.json";

    /// <summary>Erases the previous run's artifacts and the manifest — called by the producing flow on start.</summary>
    public static void Reset()
    {
        try
        {
            foreach (var file in Files())
                if (File.Exists(file))
                    File.Delete(file);

            if (File.Exists(ManifestPath))
                File.Delete(ManifestPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ArtifactStore] failed to clear: {ex.Message}");
        }
    }

    /// <summary>Writes <c>.harness/&lt;name&gt;.md</c> and registers the path in the manifest (once, in arrival order).</summary>
    public static string Write(string name, string content)
    {
        var path = Path.Combine(Dir, $"{name}.md");

        try
        {
            Directory.CreateDirectory(Dir);
            AtomicIO.WriteAllTextAtomic(path, content);

            var files = Files().ToList();
            if (!files.Contains(path))
            {
                files.Add(path);
                SaveManifest(new ArtifactManifest(files));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ArtifactStore] failed to write {name}: {ex.Message}");
        }

        return path;
    }

    /// <summary>Paths registered in the manifest, in the order they were written.</summary>
    public static IReadOnlyList<string> Files()
    {
        try
        {
            if (File.Exists(ManifestPath))
            {
                var json = File.ReadAllText(ManifestPath);
                var manifest = JsonSerializer.Deserialize(json, HarnessJsonContext.Default.ArtifactManifest);
                if (manifest is not null)
                    return manifest.Files ?? [];
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ArtifactStore] failed to load the manifest: {ex.Message}");
        }

        return [];
    }

    /// <summary>Are there artifacts written and present on disk?</summary>
    public static bool HasArtifacts() => Files().Any(File.Exists);

    /// <summary>Reads a single artifact by name (e.g. for reinjection into prompts). "" if missing/unreadable.</summary>
    public static string Read(string name)
    {
        var path = Path.Combine(Dir, $"{name}.md");

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ArtifactStore] failed to read {name}: {ex.Message}");
            return "";
        }
    }

    /// <summary>Concatenates the artifacts in manifest order — the LLM judge's input.</summary>
    public static string ReadAll()
    {
        var sb = new StringBuilder();

        foreach (var file in Files())
        {
            try
            {
                if (File.Exists(file))
                    sb.AppendLine(File.ReadAllText(file).TrimEnd()).AppendLine();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ArtifactStore] failed to read {file}: {ex.Message}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void SaveManifest(ArtifactManifest manifest)
    {
        Directory.CreateDirectory(Dir);
        AtomicIO.WriteAllTextAtomic(ManifestPath, JsonSerializer.Serialize(manifest, HarnessJsonContext.Default.ArtifactManifest));
    }
}

/// <summary>
/// Manifest of the written artifacts. Top-level type (not nested) so it's servable by
/// System.Text.Json's source generator, a Native AOT requirement.
/// </summary>
public record ArtifactManifest(List<string> Files);
