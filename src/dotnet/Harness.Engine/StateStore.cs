using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Cada invocação do harness é um processo novo e sem memória. Este store persiste o
/// estado acumulado (contador de passos + dados de domínio) em arquivo, para que o
/// envelope trafegado pelo modelo fique mínimo — economia de tokens: o modelo passa uma
/// chave, não o estado inteiro, a cada volta do loop.
/// </summary>
public static class StateStore
{
    private const string Dir = ".harness";
    private const string FilePath = ".harness/state.json";

    /// <summary>
    /// Estado final congelado do último refinamento concluído. Existe pela mesma razão que
    /// <see cref="Trace.LastRunPath"/>: o <c>start</c> de qualquer flow reseta o
    /// <c>state.json</c> vivo, então a avaliação (que checa completude) precisa ler as chaves
    /// de domínio de um snapshot estável, não do arquivo que seu próprio <c>start</c> zerou.
    /// </summary>
    public const string LastRunStatePath = ".harness/last-run.state.json";

    /// <summary>Estado final congelado do último run de avaliação — caminho próprio, não sobrescreve o do refinamento.</summary>
    public const string LastEvaluationStatePath = ".harness/last-evaluation.state.json";

    public static HarnessState Load() => LoadFrom(FilePath);

    /// <summary>Carrega um estado de um caminho arbitrário (ex.: a evidência de um caso do golden set).</summary>
    public static HarnessState LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize(json, HarnessJsonContext.Default.HarnessState);
                if (state is not null)
                    return state with { Data = state.Data ?? new() };
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StateStore] falha ao carregar: {ex.Message}");
        }

        return new HarnessState(0, new());
    }

    public static void Save(HarnessState state)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, HarnessJsonContext.Default.HarnessState));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StateStore] falha ao salvar: {ex.Message}");
        }
    }

    public static void Reset() => Save(new HarnessState(0, new()));

    /// <summary>Congela o <c>state.json</c> vivo no destino — a evidência de completude do run concluído.</summary>
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
            Console.Error.WriteLine($"[StateStore] falha ao congelar: {ex.Message}");
        }
    }

    public static int Increment()
    {
        var state = Load();
        var next = state.Step + 1;
        Save(state with { Step = next });
        return next;
    }

    /// <summary>
    /// Soma o custo do turno ao acumulado do run e devolve o total — insumo do teto de
    /// custo em <see cref="TaskRegistry"/>. Chars de instrução emitida são a única medida:
    /// é o que a engine consegue atestar sozinha, sem depender de auto-relato do driver.
    /// </summary>
    public static int AddCost(int chars)
    {
        var state = Load();
        var next = state with { CostChars = state.CostChars + chars };
        Save(next);
        return next.CostChars;
    }

    public static void Set(string key, string value)
    {
        var state = Load();
        state.Data[key] = value;
        Save(state);
    }

    public static string? Get(string key)
    {
        var state = Load();
        return state.Data.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>Persiste o contexto do driver capturado no <c>start</c> (ver TaskRegistry).</summary>
    public static void SetContext(Dictionary<string, string> context)
    {
        var state = Load();
        Save(state with { Context = context });
    }

    /// <summary>Contexto do driver persistido, para o PromptFormatter reinjetar em toda saída.</summary>
    public static Dictionary<string, string>? GetContext() => Load().Context;
}
