using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Grava uma linha por volta do loop em <c>.harness/trace.jsonl</c>. É a base tanto da
/// Telemetria (#7 do diagrama) quanto do Evaluator de trajetória (#6): o <see cref="StateStore"/>
/// guarda só o estado final — sobrescreve o <c>Data</c> a cada passo —, então sem esta
/// sequência gravada não há como avaliar o caminho que o agente percorreu.
///
/// Custo: zero token e uma escrita append por invocação.
/// </summary>
public static class Trace
{
    private const string Dir = ".harness";
    private const string FilePath = ".harness/trace.jsonl";

    /// <summary>
    /// Trajetória congelada do último refinamento que terminou em <c>stop</c>. O
    /// <see cref="HarnessHost"/> grava aqui ao concluir o flow produtor, para que outro flow
    /// (a avaliação) leia a evidência mesmo depois de resetar o <c>trace.jsonl</c> vivo no
    /// próprio <c>start</c>.
    /// </summary>
    public const string LastRunPath = ".harness/last-run.trace.jsonl";

    /// <summary>
    /// Trajetória congelada do último run de <b>avaliação</b>. Caminho próprio para que a
    /// avaliação (que também termina em <c>stop</c>) não sobrescreva a evidência do
    /// refinamento em <see cref="LastRunPath"/> — do contrário, uma reavaliação leria o
    /// trace da avaliação anterior e reprovaria a trajetória espuriamente.
    /// </summary>
    public const string LastEvaluationPath = ".harness/last-evaluation.trace.jsonl";

    /// <summary>Trunca o trace no início de um novo workflow (junto do <see cref="StateStore.Reset"/>).</summary>
    public static void Reset()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Trace] falha ao limpar: {ex.Message}");
        }
    }

    public static void Append(int step, string command, string outcome, int instructionChars)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var entry = new TraceEntry(step, command, outcome, instructionChars, DateTimeOffset.UtcNow);
            var line = JsonSerializer.Serialize(entry, HarnessJsonContext.Default.TraceEntry);
            File.AppendAllText(FilePath, line + "\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Trace] falha ao gravar: {ex.Message}");
        }
    }

    /// <summary>Congela o trace vivo no caminho de destino — a evidência do run concluído.</summary>
    public static void Snapshot(string destination)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Directory.CreateDirectory(Dir);
                File.Copy(FilePath, destination, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Trace] falha ao congelar: {ex.Message}");
        }
    }

    /// <summary>Relê o trace vivo na ordem em que foi gravado.</summary>
    public static IReadOnlyList<TraceEntry> Load() => LoadFrom(FilePath);

    /// <summary>Relê um trace de um caminho arbitrário — insumo dos evaluators (ex.: o snapshot).</summary>
    public static IReadOnlyList<TraceEntry> LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];

            return File.ReadAllLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize(line, HarnessJsonContext.Default.TraceEntry))
                .OfType<TraceEntry>()
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Trace] falha ao carregar: {ex.Message}");
            return [];
        }
    }
}

/// <summary>
/// Uma volta do loop: passo, comando recebido, desfecho, custo (chars da instrução emitida)
/// e horário de gravação. O timestamp não é dado de token — é só quando o passo aconteceu,
/// mesma categoria de <see cref="Step"/>/<see cref="Outcome"/> — mas dá a chave temporal que
/// falta para correlacionar cada passo com os tokens reais que o driver gastou decidindo-o
/// (ver scripts/harness_cost_correlate.py), sem o harness precisar auto-relatar tokens.
/// </summary>
public record TraceEntry(int Step, string Command, string Outcome, int InstructionChars, DateTimeOffset Timestamp);

/// <summary>Desfechos possíveis de um passo, gravados em <see cref="TraceEntry.Outcome"/>.</summary>
public static class TraceOutcome
{
    public const string Instruction = "instruction"; // seguiu para o próximo passo
    public const string Stop = "stop";               // término normal do flow
    public const string Error = "error";             // erro tipado devolvido ao driver
    public const string Budget = "budget";           // corte pelo teto de passos
    public const string Timeout = "timeout";          // corte pelo teto de tempo por passo
}
