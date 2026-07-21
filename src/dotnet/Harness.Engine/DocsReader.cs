using System.Text;

namespace Harness.Engine;

/// <summary>
/// Lê um conjunto de documentos (`*.md` e `*.txt`) de uma pasta para injetar no prompt.
/// É a entrada alternativa ao input interativo: o flow lê o material já existente
/// (specs, notas, transcrições) e o modelo sintetiza um brief a partir dele.
///
/// Análogo a como <see cref="PromptFormatter"/> injeta skills — a leitura é determinística
/// (feita em código), só a síntese fica com o modelo.
/// </summary>
public static class DocsReader
{
    // Teto de caracteres: injetar docs gigantes queima tokens de forma silenciosa, e o
    // repo mede tokens (ver bench/). Ao exceder, trunca e avisa no stderr.
    // Valor vem do harness.json (ou do default) — ver HarnessConfig.
    private static int MaxChars => HarnessConfig.Current.DocsMaxChars;

    private static readonly string[] Extensions = [".md", ".txt"];

    /// <summary>Existe a pasta e há ao menos um arquivo `*.md`/`*.txt`?</summary>
    public static bool HasDocs(string folder)
    {
        var dir = PathResolver.Resolve(folder);
        return Directory.Exists(dir) && Files(dir).Length > 0;
    }

    /// <summary>
    /// Concatena os documentos em ordem alfabética, cada um sob um cabeçalho
    /// `## &lt;nome-do-arquivo&gt;`, e devolve também a lista de nomes (para citar as fontes).
    /// </summary>
    public static (string Content, string[] Files) Read(string folder)
    {
        var dir = PathResolver.Resolve(folder);
        if (!Directory.Exists(dir))
            return (string.Empty, []);

        var files = Files(dir);
        var names = new List<string>(files.Length);
        var sb = new StringBuilder();

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DocsReader] falha ao ler {name}: {ex.Message}");
                continue;
            }

            names.Add(name);
            sb.Append("## ").AppendLine(name).AppendLine().AppendLine(text).AppendLine();

            if (sb.Length > MaxChars)
            {
                Console.Error.WriteLine(
                    $"[DocsReader] conteúdo excedeu {MaxChars} chars; truncando em {name}.");
                sb.Length = MaxChars;
                break;
            }
        }

        return (sb.ToString().TrimEnd(), names.ToArray());
    }

    private static string[] Files(string dir) =>
        Directory.EnumerateFiles(dir)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
