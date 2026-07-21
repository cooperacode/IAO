using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Engine;

/// <summary>
/// A lista de features do flow de desenvolvimento, persistida em
/// <c>.harness/feature_list.json</c> — o "persistent artifact" que atravessa os hard resets
/// de contexto: cada sessão (uma feature) lê e escreve aqui, sem depender do histórico da
/// conversa. Todas nascem com <see cref="Feature.Passes"/> = false; o flow vira uma por vez
/// até não sobrar nenhuma pendente.
///
/// Mora na engine porque a serialização AOT-safe depende do <see cref="HarnessJsonContext"/>,
/// interno ao assembly. Mesma tolerância dos demais stores: ausente ou ilegível → lista vazia,
/// nunca derruba o run.
/// </summary>
public static class FeatureStore
{
    private const string Dir = ".harness";
    private const string FilePath = ".harness/feature_list.json";

    /// <summary>Sobrescreve a lista inteira — usada pelo <c>plan</c> (session 0) e por MarkPassed.</summary>
    public static void Write(IReadOnlyList<Feature> features)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(
                new FeatureList([.. features]), HarnessJsonContext.Default.FeatureList);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FeatureStore] falha ao gravar: {ex.Message}");
        }
    }

    /// <summary>
    /// Interpreta o array cru de features que o driver devolve no <c>plan</c>
    /// (<c>[{"id":1,"title":"...","priority":1}, ...]</c>). Força <c>Passes = false</c> (toda
    /// feature nasce pendente) e reindexa ids ausentes/duplicados pela ordem. Lista vazia se o
    /// JSON não interpretar — o caller re-emite o pedido (loop corretivo), não derruba o run.
    /// O parse vive na engine porque o <see cref="HarnessJsonContext"/> (AOT) é interno ao assembly.
    /// </summary>
    public static IReadOnlyList<Feature> Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize(json, HarnessJsonContext.Default.FeatureArray);
            if (parsed is null || parsed.Length == 0)
                return [];

            // Reindex primeiro: DependsOn só faz sentido referenciando ids já finais, não os
            // brutos (possivelmente ausentes/duplicados) que vieram do driver.
            var reindexed = parsed
                .Select((f, i) => f with { Id = f.Id > 0 ? f.Id : i + 1, Passes = false, DependsOn = f.Deps })
                .ToList();

            if (DependencyGraphError(reindexed) is { } error)
            {
                Console.Error.WriteLine($"[FeatureStore] grafo de dependências inválido: {error}");
                return [];
            }

            return reindexed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FeatureStore] falha ao interpretar features: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// <c>null</c> se o grafo de <c>DependsOn</c> é válido (todo id existe, sem ciclo); senão,
    /// uma descrição do problema. Kahn (ordenação topológica): sobra nó fora do conjunto
    /// resolvido ⇒ ciclo. Checa dangling ref primeiro — senão uma dependência fantasma seria
    /// contada como eternamente não-resolvida e reportada como "ciclo" quando na verdade é id
    /// inválido. Usa GroupBy/lookup tolerante (não <c>ToDictionary</c> direto) porque ids
    /// duplicados não são deduplicados hoje pelo reindex acima — não é escopo desta mudança
    /// corrigir isso, só não lançar por causa disso.
    /// </summary>
    private static string? DependencyGraphError(IReadOnlyList<Feature> features)
    {
        var validIds = features.Select(f => f.Id).ToHashSet();

        var dangling = features
            .SelectMany(f => f.Deps.Where(dep => !validIds.Contains(dep)).Select(dep => $"{f.Id}->{dep}"))
            .ToList();
        if (dangling.Count > 0)
            return $"dependsOn referencia id(s) inexistente(s): {string.Join(", ", dangling)}";

        var indegree = features.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Deps.Length);
        var dependents = features.SelectMany(f => f.Deps.Select(dep => (dep, f.Id))).ToLookup(x => x.dep, x => x.Id);

        var queue = new Queue<int>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var resolved = new HashSet<int>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!resolved.Add(id))
                continue;

            foreach (var dependent in dependents[id])
                if (indegree.ContainsKey(dependent) && --indegree[dependent] == 0)
                    queue.Enqueue(dependent);
        }

        return resolved.Count == indegree.Count
            ? null
            : $"dependência cíclica entre as features: {string.Join(", ", indegree.Keys.Except(resolved))}";
    }

    public static IReadOnlyList<Feature> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];

            var json = File.ReadAllText(FilePath);
            var list = JsonSerializer.Deserialize(json, HarnessJsonContext.Default.FeatureList);
            return list?.Items ?? [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FeatureStore] falha ao carregar: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// A próxima feature a implementar: a de maior prioridade (menor <see cref="Feature.Priority"/>)
    /// entre as PRONTAS (todo id em <see cref="Feature.Deps"/> já com <c>Passes == true</c>);
    /// desempate por <see cref="Feature.Id"/>. <c>null</c> quando não há pendência pronta — pode
    /// significar fim de fato (nenhuma pendência) ou dependências bloqueadas (ver <c>Pick</c> em
    /// <c>Flows.Development</c>). "Ready set" de Kahn recalculado a cada chamada sobre a lista
    /// carregada — sem estrutura de grafo persistida.
    /// </summary>
    public static Feature? NextPending()
    {
        var features = Load();
        var passed = features.Where(f => f.Passes).Select(f => f.Id).ToHashSet();

        return features
            .Where(f => !f.Passes && f.Deps.All(passed.Contains))
            .OrderBy(f => f.Priority)
            .ThenBy(f => f.Id)
            .FirstOrDefault();
    }

    /// <summary>Marca a feature como concluída e regrava a lista. No-op se o id não existe.</summary>
    public static void MarkPassed(int id)
    {
        var features = Load();
        if (features.All(f => f.Id != id))
            return;

        Write([.. features.Select(f => f.Id == id ? f with { Passes = true } : f)]);
    }

    /// <summary>Quantas features ainda faltam (<c>Passes == false</c>).</summary>
    public static int PendingCount() => Load().Count(f => !f.Passes);

    /// <summary>Há features e todas passaram — condição de término do loop.</summary>
    public static bool AllPassing()
    {
        var features = Load();
        return features.Count > 0 && features.All(f => f.Passes);
    }

    /// <summary>Apaga a lista do run anterior — o flow PRODUTOR reseta no seu <c>start</c>.</summary>
    public static void Reset()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FeatureStore] falha ao limpar: {ex.Message}");
        }
    }
}

/// <summary>Uma feature do backlog de desenvolvimento: prioridade (menor = mais alta), se já passa
/// e de quais outras (por id) depende.</summary>
///
/// <remarks>
/// <c>DependsOn</c> é ANULÁVEL de propósito: <c>= []</c> não é constante em tempo de compilação
/// (não pode ser default de parâmetro posicional do record); e uma propriedade <c>init</c> extra
/// fora do construtor tem o inicializador IGNORADO pelo <see cref="System.Text.Json"/> quando a
/// chave não existe no JSON — grava <c>null</c>, não <c>[]</c>. <see cref="Deps"/> normaliza isso
/// para quem consome; um <c>feature_list.json</c> gravado por uma versão anterior do harness
/// (sem <c>dependsOn</c>) continua carregando sem lançar.
/// </remarks>
public record Feature(int Id, string Title, int Priority, bool Passes, int[]? DependsOn = null)
{
    [JsonIgnore]
    public int[] Deps => DependsOn ?? [];
}

/// <summary>
/// Envelope de topo da <c>feature_list.json</c>. Tipo dedicado (não <c>List&lt;Feature&gt;</c> solto)
/// para ser servível pelo source generator do System.Text.Json, requisito do Native AOT.
/// </summary>
public record FeatureList(List<Feature> Items);
