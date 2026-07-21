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
        try
        {
            var path = PathResolver.Resolve(FilePath);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize(json, HarnessJsonContext.Default.HarnessConfig);
                if (config is not null)
                    return Normalize(config);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HarnessConfig] falha ao carregar; usando defaults: {ex.Message}");
        }

        return Default;
    }

    // Um harness.json parcial deserializa os campos ausentes como 0/null. Zero é válido só
    // onde significa "desligado" (tetos de custo); nos demais, campo ausente = default.
    private static HarnessConfig Normalize(HarnessConfig config) => config with
    {
        MaxSteps = config.MaxSteps > 0 ? config.MaxSteps : Default.MaxSteps,
        MaxInstructionChars = int.Max(config.MaxInstructionChars, 0),
        DocsMaxChars = config.DocsMaxChars > 0 ? config.DocsMaxChars : Default.DocsMaxChars,
        DocsFolder = string.IsNullOrWhiteSpace(config.DocsFolder) ? Default.DocsFolder : config.DocsFolder,
        TimeoutMs = int.Max(config.TimeoutMs, 0),
    };
}
