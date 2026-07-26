using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Persiste <c>verify_cmd</c>/<c>target_dir</c> (capturados uma vez pelo <c>plan</c>) em
/// <c>.harness/run_config.json</c> — fora de <c>state.json</c> de propósito. O
/// <see cref="TaskRegistry"/> reseta <c>state.json</c> incondicionalmente a cada <c>start</c>,
/// antes de qualquer código de domínio rodar; um run retomado (ver
/// <c>Flows.Development.DevelopmentTasks.Start</c>) ainda precisa desses dois valores para
/// <c>smoke</c>/<c>verify</c> funcionarem, então eles têm que sobreviver a esse reset.
/// </summary>
public static class RunConfigStore
{
    private const string Dir = ".harness";
    private const string FilePath = ".harness/run_config.json";

    /// <summary>Grava a configuração do run — mesmo ciclo de vida da feature_list.json (escrita
    /// pelo <c>plan</c>, apagada só quando <c>start</c> decide que não há run para retomar).</summary>
    public static void Write(RunConfig config)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            AtomicIO.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(config, HarnessJsonContext.Default.RunConfig));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RunConfigStore] falha ao gravar: {ex.Message}");
        }
    }

    /// <summary>Lê a configuração persistida, ou os defaults se nada foi gravado ainda.</summary>
    public static RunConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var config = JsonSerializer.Deserialize(json, HarnessJsonContext.Default.RunConfig);
                if (config is not null)
                    return config;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RunConfigStore] falha ao carregar: {ex.Message}");
        }

        return new RunConfig();
    }

    /// <summary>Apaga num run genuinamente novo — em par com FeatureStore.Reset().</summary>
    public static void Reset()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RunConfigStore] falha ao limpar: {ex.Message}");
        }
    }
}

/// <summary>
/// Comando de verificação, diretório-alvo e identidade do run (RFC §6.4), todos capturados
/// uma vez pelo <c>plan</c>. <see cref="RunId"/> é gerado só num run genuinamente novo — o
/// mesmo momento em que <see cref="RunConfigStore.Write"/> é chamado após
/// <see cref="RunConfigStore.Reset"/> — e sobrevive a toda retomada porque este arquivo não é
/// tocado quando <c>start</c> decide que há trabalho pendente (ver comentário da classe).
/// Terceiro parâmetro posicional com default <c>""</c> para não quebrar os
/// <c>new RunConfig(verifyCmd, targetDir)</c> já espalhados pelos testes.
/// </summary>
public record RunConfig(string VerifyCmd = "", string TargetDir = ".", string RunId = "");
