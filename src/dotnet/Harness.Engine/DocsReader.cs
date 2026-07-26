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
    // Teto de octetos UTF-8 (RFC Apêndice B item 1: medir em bytes, não em chars .NET, para
    // que o teto tenha o mesmo significado entre engines .NET/Python/Rust): injetar docs
    // gigantes queima tokens de forma silenciosa, e o repo mede tokens (ver bench/). Ao
    // exceder, trunca em fronteira de byte líder válida e avisa no stderr. O nome do campo
    // (DocsMaxChars, vindo do harness.json/HarnessConfig) não muda — só a unidade medida.
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

            if (Encoding.UTF8.GetByteCount(sb.ToString()) > MaxChars)
            {
                Console.Error.WriteLine(
                    $"[DocsReader] conteúdo excedeu {MaxChars} bytes (UTF-8); truncando em {name}.");
                var truncated = TruncateUtf8Bytes(sb.ToString(), MaxChars);
                sb.Clear().Append(truncated);
                break;
            }
        }

        return (sb.ToString().TrimEnd(), names.ToArray());
    }

    /// <summary>
    /// Corta <paramref name="text"/> em no máximo <paramref name="maxBytes"/> octetos UTF-8,
    /// recuando até uma fronteira de byte líder válida — nunca parte um caractere multibyte
    /// (acento, emoji) ao meio, o que produziria bytes inválidos/replacement characters.
    /// </summary>
    private static string TruncateUtf8Bytes(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return text;

        var cut = maxBytes;
        // Byte de continuação UTF-8 tem os dois bits mais significativos "10" (0x80..0xBF);
        // recuar até um byte que NÃO é continuação garante que [0, cut) é uma sequência completa.
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
            cut--;

        return Encoding.UTF8.GetString(bytes, 0, cut);
    }

    private static string[] Files(string dir) =>
        Directory.EnumerateFiles(dir)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
