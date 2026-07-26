namespace Harness.Engine;

/// <summary>
/// Escrita "final" atômica para os stores em <c>.harness</c>: grava num arquivo temporário no
/// MESMO diretório do destino e troca via <see cref="File.Move(string, string, bool)"/> com
/// <c>overwrite: true</c> — atômico na mesma partição desde .NET Core 3.0+. Evita que um
/// crash/kill no meio da escrita deixe o arquivo final truncado ou parcialmente sobrescrito;
/// um leitor concorrente sempre vê a versão anterior completa ou a nova completa, nunca um
/// estado intermediário. Não se aplica aos <c>File.AppendAllText</c> de log/trace — esses já
/// são atômicos no nível do evento (uma linha, uma chamada) e não precisam de troca de arquivo.
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

    /// <summary>Mesma garantia atômica de <see cref="WriteAllTextAtomic"/>, mas copiando de um arquivo-fonte existente (ex.: snapshot de um store vivo para o seu congelado).</summary>
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

    // Nome único por escrita no MESMO diretório do destino — Path.GetTempFileName() não serve
    // porque cria fora dessa pasta, quebrando a garantia de rename atômico (mesma partição).
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
            // Limpeza é best-effort — não mascara a exceção original que já está sendo relançada.
        }
    }
}
