using Harness.Engine;

namespace Flows.Development;

/// <summary>
/// Flow de desenvolvimento long-running (padrão "Effective harnesses for long-running agents",
/// Anthropic). Um inicializador (session 0) expande o brief numa lista priorizada de features;
/// depois um loop de sessões de contexto fresco implementa UMA feature por vez:
///
///   start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
///
/// O estado que atravessa os hard resets vive em artefatos persistentes: a
/// <see cref="FeatureStore"/> (feature_list.json, do harness) e o progress.txt + git
/// (do diretório-alvo). Cada task só faz efeitos e decide o PRÓXIMO comando (o <c>output</c> Envelope)
/// — a orquestração (dispatch, guardas globais, transporte) fica em Harness.Engine.
///
/// Prompts em <c>DevelopmentTasks.Prompt.cs</c> (partial).
/// </summary>
public static partial class DevelopmentTasks
{
    // Guardas locais deste flow (o teto global do harness.json, 12, é curto demais p/ um loop).
    // Poucas features + teto de passos POR feature: barra o loop implement↔verify que nunca fecha.
    public const int MaxFeatures = 10;
    public const int StepsPerFeature = 8;

    // Teto de passos efetivo passado ao HarnessHost (override do global): folga p/ o pior caso
    // de MaxFeatures features gastando StepsPerFeature cada, mais o start/plan e as fronteiras.
    public const int StepBudget = MaxFeatures * StepsPerFeature + 8;

    // Chaves do StateStore.Data usadas pelos arquivos parciais deste flow (Handoff/Prompt/
    // Verify) — const em vez de string literal repetida, para que um typo em qualquer um
    // dos arquivos vire erro de compilação em vez de uma chave nunca lida.
    private const string CurrentFeatureIdKey = "current_feature_id";
    private const string CurrentFeatureTitleKey = "current_feature_title";
    private const string CurrentFeatureSummaryKey = "current_feature_summary";
    private const string CurrentFeatureVerifyKey = "current_feature_verify";
    private const string FeatureStepsKey = "feature_steps";

    // Nome do artefato do brief no ArtifactStore (.harness/brief.md) — persistido em Start()
    // para poder ser reinjetado em bearings/implement (DevelopmentTasks.Prompt.cs), já que o
    // conteúdo lido de docs/ hoje só existia como variável local do turno do inicializador.
    private const string BriefArtifactName = "brief";

    private static string State(string key) => StateStore.Get(key) ?? "";
    private static string DocsFolder => HarnessConfig.Current.DocsFolder;

    public static string Start()
    {
        // Uma sessão anterior (talvez de outro driver — os tokens acabaram numa IDE e outra
        // assume) pode ter morrido no meio de uma feature. Reiniciar jogaria fora trabalho em
        // andamento; retomar é seguro e determinístico: Bearings é reentrante por construção
        // (só rearma a guarda por feature) e o próximo Pick() reseleciona a mesma feature,
        // ainda pendente — sem precisar saber exatamente onde a sessão anterior parou.
        if (FeatureStore.PendingCount() > 0)
        {
            Console.Error.WriteLine(
                "[dev] run em andamento detectado (feature pendente); retomando via bearings em vez de resetar.");
            return BearingsPrompt();
        }

        // Flow PRODUTOR da feature_list: novo run apaga a do run anterior.
        FeatureStore.Reset();
        RunConfigStore.Reset();
        // Sem isto, um run novo no modo interativo (sem docs/) herdaria silenciosamente o
        // brief.md de um run anterior — o modo interativo nunca chama ArtifactStore.Write,
        // então só este Reset garante que nenhum brief de um tópico antigo sobrevive.
        ArtifactStore.Reset();

        // Brief (o que construir) vem de docs/ ou, sem docs, do modo interativo.
        if (!DocsReader.HasDocs(DocsFolder))
            return InitializerInteractive();

        var (content, files) = DocsReader.Read(DocsFolder);
        // Persistido para ser reinjetado em bearings/implement (DevelopmentTasks.Prompt.cs) —
        // antes desta feature, "content" era só uma variável local deste turno, descartada
        // assim que o inicializador terminava.
        ArtifactStore.Write(BriefArtifactName, content);
        StateStore.Set("origem", "docs");
        return InitializerPrompt(content, files);
    }

    public static string Plan(Envelope? envelope)
    {
        var features = FeatureStore.Parse(Arg(envelope));
        if (features.Count == 0)
            return PlanRetryPrompt(); // não interpretou → re-pede (loop corretivo)

        // Teto de features: fica com as de maior prioridade (menor número).
        var capped = features.OrderBy(f => f.Priority).ThenBy(f => f.Id).Take(MaxFeatures).ToList();

        // Higieniza DependsOn: uma feature sobrevivente pode depender de um id cortado acima,
        // o que a bloquearia para sempre (nunca "pronta") sem que o driver tenha como saber —
        // quem cortou foi o harness, não ele. Cortar nós de um grafo já acíclico (validado em
        // FeatureStore.Parse) não pode criar ciclo, então só a limpeza de dangling é necessária.
        var cappedIds = capped.Select(f => f.Id).ToHashSet();
        capped = [.. capped.Select(f => f with { DependsOn = f.Deps.Where(cappedIds.Contains).ToArray() })];

        FeatureStore.Write(capped);

        // Comando de verificação, diretório-alvo e identidade do run: reidratados a cada passo
        // de smoke/verify. Fora de state.json de propósito - ver RunConfigStore. RunId nasce
        // aqui (mesmo instante em que Start() decidiu que este é um run novo, não retomado) e
        // sobrevive a toda sessão seguinte sem precisar aparecer no Envelope trocado com o
        // modelo (RFC §6.4 — identidade do run é concern do control plane, não do contrato).
        RunConfigStore.Write(new RunConfig(
            ArgAt(envelope, 1, "dotnet test"),
            ArgAt(envelope, 2, "."),
            Guid.NewGuid().ToString()));

        return BearingsPrompt();
    }

    public static string Bearings(Envelope? envelope)
    {
        // Nova sessão (uma feature): zera o contador da guarda por feature.
        StateStore.Set(FeatureStepsKey, "1");
        return SmokePrompt();
    }

    public static string Smoke(Envelope? envelope) =>
        OverFeatureBudget() ? Stop("guarda por feature") : PickPrompt();

    public static string Pick(Envelope? envelope)
    {
        if (OverFeatureBudget())
            return Stop("guarda por feature");

        // Seleção DETERMINÍSTICA: maior prioridade entre as prontas (dependências satisfeitas).
        // O harness escolhe, não o LLM.
        var next = FeatureStore.NextPending();
        if (next is null)
        {
            // PendingCount() == 0 é o caso normal (handoff já teria fechado antes). Pendência
            // > 0 só é alcançável por um feature_list.json editado à mão fora do grafo validado
            // no plan (Write/MarkPassed não revalidam) — não finge sucesso nesse caso.
            return FeatureStore.PendingCount() == 0
                ? Done()
                : Stop("dependências bloqueadas — nenhuma feature pendente está pronta");
        }

        StateStore.Set(CurrentFeatureIdKey, next.Id.ToString());
        StateStore.Set(CurrentFeatureTitleKey, next.Title);
        // Etiqueta o trace com a feature corrente (ver TraceEntry.Label) — sem isso, cada
        // linha do trace.jsonl só tem o Step global, sem dizer a qual feature ele pertence.
        StateStore.Set(StateStore.TraceLabelKey, $"feature:{next.Id}");
        return ImplementPrompt(next);
    }

    public static string Implement(Envelope? envelope)
    {
        if (OverFeatureBudget())
            return Stop("guarda por feature");

        var summary = Arg(envelope).Trim();
        if (!string.IsNullOrWhiteSpace(summary))
            StateStore.Set(CurrentFeatureSummaryKey, summary);

        var autoVerify = TryAutomatedVerify();
        if (autoVerify.Attempted)
        {
            StateStore.Set(CurrentFeatureVerifyKey, autoVerify.Result);
            return autoVerify.Success
                ? CompleteVerifiedFeature(autoVerify.Result)
                : FixPrompt(autoVerify.Result);
        }

        return VerifyPrompt();
    }

    public static string Verify(Envelope? envelope)
    {
        if (OverFeatureBudget())
            return Stop("guarda por feature");

        // FALHOU → volta a implementar a MESMA feature (loop de correção, limitado pela guarda).
        // PASSOU → o harness faz o handoff determinístico (progress + git) sem gastar um
        // turno do modelo; se falhar, cai no prompt legado de reparo manual.
        var result = Arg(envelope).Trim();
        if (result.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase))
            return FixPrompt(result);

        if (result.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
        {
            StateStore.Set(CurrentFeatureVerifyKey, result);
            return CompleteVerifiedFeature(result);
        }

        return VerifyRetryPrompt();
    }

    public static string Handoff(Envelope? envelope)
    {
        if (string.IsNullOrWhiteSpace(Arg(envelope)))
            return HandoffRetryPrompt();

        if (int.TryParse(State(CurrentFeatureIdKey), out var id))
            FeatureStore.MarkPassed(id);

        // Alguma feature ainda pendente? Sim → próxima sessão (bearings). Não → fim.
        return FeatureStore.AllPassing() ? Done() : BearingsPrompt();
    }

    // --- guardas e término -------------------------------------------------

    /// <summary>Incrementa o contador da sessão e sinaliza estouro do teto por feature.</summary>
    private static bool OverFeatureBudget()
    {
        var steps = (int.TryParse(State(FeatureStepsKey), out var s) ? s : 0) + 1;
        StateStore.Set(FeatureStepsKey, steps.ToString());

        if (steps > StepsPerFeature)
        {
            Console.Error.WriteLine(
                $"[dev] feature '{State(CurrentFeatureTitleKey)}' excedeu {StepsPerFeature} passos; encerrando.");
            return true;
        }
        return false;
    }

    private static string Stop(string motivo)
    {
        Console.Error.WriteLine($"[dev] encerrado por {motivo}. feature_list em .harness/feature_list.json");
        return "stop";
    }

    private static string Done()
    {
        Console.Error.WriteLine(
            $"[dev] todas as {FeatureStore.Load().Count} features passam; concluído. "
            + "Estado em .harness/feature_list.json");
        return "stop";
    }

    private static string Arg(Envelope? envelope) =>
        envelope?.Args is { Length: > 0 } ? envelope.Args[0] : string.Empty;

    private static string ArgAt(Envelope? envelope, int index, string fallback) =>
        envelope?.Args is { } args && args.Length > index && !string.IsNullOrWhiteSpace(args[index])
            ? args[index]
            : fallback;
}
