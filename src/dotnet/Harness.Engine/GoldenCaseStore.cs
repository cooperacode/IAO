using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Carrega os casos do golden set do disco. Mora na engine porque a desserialização
/// AOT-safe depende do <see cref="HarnessJsonContext"/>, interno ao assembly.
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
            Console.Error.WriteLine($"[GoldenCaseStore] falha ao carregar {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Carrega todos os <c>*.json</c> de um diretório, ordenados por nome, ignorando os inválidos.</summary>
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
