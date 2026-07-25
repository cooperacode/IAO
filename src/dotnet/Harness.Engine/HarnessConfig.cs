using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Variáveis fixas do harness, externalizadas num <c>harness.json</c> na raiz do repo. Antes
/// eram constantes hardcoded espalhadas (<see cref="TaskRegistry"/>, <see cref="DocsReader"/>,
/// <c>RefinementTasks</c>); centralizá-las aqui deixa cada flow/ambiente ajustar os tetos sem
/// recompilar. Ausente ou ilegível → cai em <see cref="Default"/> (mesma tolerância de
/// <see cref="StateStore.LoadFrom"/>: config é insumo opcional, não pode derrubar o run).
///
/// Tipo de topo (não aninhado) para ser servível pelo source generator do System.Text.Json,
/// requisito do Native AOT.
/// </summary>
public record HarnessConfig(
    int MaxSteps,
    int MaxInstructionChars,
    int DocsMaxChars,
    string DocsFolder,
    int TimeoutMs)
{
    // Teto de passos: impede loop infinito que queimaria tokens indefinidamente.
    // MaxInstructionChars = 0 desliga o teto de custo (só o de passos vale).
    // TimeoutMs = 0 desliga a guarda de tempo por passo (mesma convenção do custo). O valor
    // ligado vive no harness.json shipado, NÃO aqui: se o Default fosse > 0, um harness.json
    // que omitisse o campo (deserializa 0) nunca conseguiria significar "desligado".
    public static HarnessConfig Default { get; } = new(
        MaxSteps: 12,
        MaxInstructionChars: 0,
        DocsMaxChars: 40_000,
        DocsFolder: "docs",
        TimeoutMs: 0);

    private const string FilePath = "harness.json";

    // Teto duro do timeoutMs, independente da fonte (harness.json OU a env var abaixo).
    // harness.json vive no working directory que o próprio agente supervisionado controla:
    // sem este teto, o agente poderia editar o arquivo para se auto-conceder um timeout
    // arbitrariamente alto e nunca ser cortado pela guarda de tempo (ver TaskRegistry).
    private const int MaxAllowedTimeoutMs = 5 * 60_000;

    // Quando definida, sobrepõe o timeoutMs do harness.json. Ao contrário do arquivo, a env
    // var é definida pelo processo pai que invoca cada passo do harness — fora do working
    // directory que o agente supervisionado controla — então não pode ser auto-editada pelo
    // mesmo agente que o timeout deveria conter.
    private const string TimeoutMsEnvVar = "HARNESS_TIMEOUT_MS";

    // Carregada uma vez por processo (cada invocação do harness é um processo novo, então
    // "uma vez" = "por volta do loop"). Leitores estáticos — DocsReader, RefinementTasks —
    // consomem daqui sem precisar receber a config por parâmetro.
    private static HarnessConfig? _current;
    public static HarnessConfig Current => _current ??= Load();

    /// <summary>Força a releitura do <c>harness.json</c> — para testes e drivers de longa vida.</summary>
    public static void Reload() => _current = Load();

    /// <summary>Relê o <c>harness.json</c> do disco; qualquer falha devolve <see cref="Default"/>.</summary>
    public static HarnessConfig Load()
    {
        var config = Default;
        try
        {
            var path = PathResolver.Resolve(FilePath);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize(json, HarnessJsonContext.Default.HarnessConfig);
                if (parsed is not null)
                    config = parsed;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HarnessConfig] falha ao carregar; usando defaults: {ex.Message}");
            config = Default;
        }

        return Normalize(ApplyTimeoutEnvOverride(config));
    }

    // Ver TimeoutMsEnvVar. Ausente/inválida é ignorada silenciosamente — mesma tolerância do
    // resto da config: é insumo opcional, não pode derrubar o run.
    private static HarnessConfig ApplyTimeoutEnvOverride(HarnessConfig config)
    {
        var raw = Environment.GetEnvironmentVariable(TimeoutMsEnvVar);
        return int.TryParse(raw, out var timeoutMs) ? config with { TimeoutMs = timeoutMs } : config;
    }

    // Um harness.json parcial deserializa os campos ausentes como 0/null. Zero é válido só
    // onde significa "desligado" (tetos de custo); nos demais, campo ausente = default.
    private static HarnessConfig Normalize(HarnessConfig config) => config with
    {
        MaxSteps = config.MaxSteps > 0 ? config.MaxSteps : Default.MaxSteps,
        MaxInstructionChars = int.Max(config.MaxInstructionChars, 0),
        DocsMaxChars = config.DocsMaxChars > 0 ? config.DocsMaxChars : Default.DocsMaxChars,
        DocsFolder = string.IsNullOrWhiteSpace(config.DocsFolder) ? Default.DocsFolder : config.DocsFolder,
        TimeoutMs = int.Clamp(config.TimeoutMs, 0, MaxAllowedTimeoutMs),
    };
}
