using System.Text;
using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Persiste cada artefato do flow no seu próprio arquivo (<c>.harness/&lt;nome&gt;.md</c>) e
/// mantém um manifesto (<c>.harness/artifacts.json</c>) com a ordem de gravação. O manifesto
/// é o contrato entre produtor e consumidor: a avaliação lê os artefatos por ele, sem
/// depender de um relatório combinado.
///
/// Só o flow PRODUTOR reseta o manifesto (no seu <c>start</c>) — o consumidor (avaliação)
/// não toca nele, pela mesma razão dos snapshots de <see cref="Trace"/>/<see cref="StateStore"/>:
/// o start do avaliador não pode apagar a evidência que ele mesmo vai ler.
/// </summary>
public static class ArtifactStore
{
    private const string Dir = ".harness";
    public const string ManifestPath = ".harness/artifacts.json";

    /// <summary>Apaga os artefatos do run anterior e o manifesto — chamado pelo flow produtor no start.</summary>
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
            Console.Error.WriteLine($"[ArtifactStore] falha ao limpar: {ex.Message}");
        }
    }

    /// <summary>Grava <c>.harness/&lt;nome&gt;.md</c> e registra o caminho no manifesto (uma vez, em ordem de chegada).</summary>
    public static string Write(string name, string content)
    {
        var path = Path.Combine(Dir, $"{name}.md");

        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(path, content);

            var files = Files().ToList();
            if (!files.Contains(path))
            {
                files.Add(path);
                SaveManifest(new ArtifactManifest(files));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ArtifactStore] falha ao gravar {name}: {ex.Message}");
        }

        return path;
    }

    /// <summary>Caminhos registrados no manifesto, na ordem em que foram gravados.</summary>
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
            Console.Error.WriteLine($"[ArtifactStore] falha ao carregar manifesto: {ex.Message}");
        }

        return [];
    }

    /// <summary>Há artefatos gravados e presentes no disco?</summary>
    public static bool HasArtifacts() => Files().Any(File.Exists);

    /// <summary>Concatena os artefatos na ordem do manifesto — o insumo do juiz-LLM.</summary>
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
                Console.Error.WriteLine($"[ArtifactStore] falha ao ler {file}: {ex.Message}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void SaveManifest(ArtifactManifest manifest)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, HarnessJsonContext.Default.ArtifactManifest));
    }
}

/// <summary>
/// Manifesto dos artefatos gravados. Tipo de topo (não aninhado) para ser servível pelo
/// source generator do System.Text.Json, requisito do Native AOT.
/// </summary>
public record ArtifactManifest(List<string> Files);
