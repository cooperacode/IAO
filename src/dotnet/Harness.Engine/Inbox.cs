namespace Harness.Engine;

/// <summary>
/// Canal de entrada por arquivo — alternativa ao argv para o envelope do turno.
///
/// O transporte por argumento single-quoted (<c>./run-refinement.sh '&lt;JSON&gt;'</c>) tem uma
/// falha estrutural: se o driver-LLM esquece a aspa de fechamento, o shell entra em modo de
/// continuação e trava ANTES do binário rodar — nenhuma validação da engine pode pegá-lo. A
/// inbox tira o payload da sintaxe de aspas do shell: o agente escreve o JSON aqui com sua
/// ferramenta de escrita de arquivo (não passa por shell) e roda o script SEM argumentos, um
/// comando bare que não tem como ficar não-terminado.
/// </summary>
public static class Inbox
{
    private const string Dir = ".harness";
    public const string Path = ".harness/inbox.json";

    // Rastro do último envelope consumido — evita reprocessar um JSON velho se o script
    // rodar duas vezes sem reescrita, e serve de diagnóstico.
    public const string ConsumedPath = ".harness/inbox.consumed.json";

    /// <summary>Conteúdo bruto da inbox, ou <c>""</c> se ela não existir. O parse/sanitização fica no <see cref="Envelope"/>.</summary>
    public static string Read()
    {
        try
        {
            if (File.Exists(Path))
                return File.ReadAllText(Path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Inbox] falha ao ler {Path}: {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>Move a inbox consumida para <see cref="ConsumedPath"/> após um parse bem-sucedido.</summary>
    public static void Consume()
    {
        try
        {
            if (File.Exists(Path))
            {
                Directory.CreateDirectory(Dir);
                File.Move(Path, ConsumedPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Inbox] falha ao consumir {Path}: {ex.Message}");
        }
    }
}
