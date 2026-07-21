using Flows.Development;
using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// O loop por feature do flow de desenvolvimento: cada task decide o PRÓXIMO comando (padrão
/// do gate de avaliação). Cobre as ramificações — verify FAIL↺implement, verify PASS→handoff,
/// handoff→bearings (próxima feature) vs. stop — e a guarda por feature.
/// </summary>
public class DevelopmentFlowTests : IDisposable
{
    // id 1 tem prioridade 2; id 2 tem prioridade 1 → a de maior prioridade é a id 2.
    private const string FeaturesJson =
        """[{"id":1,"title":"A","priority":2},{"id":2,"title":"B","priority":1}]""";

    public DevelopmentFlowTests() => Clean();
    public void Dispose() => Clean();

    private static void Clean()
    {
        StateStore.Reset();
        FeatureStore.Reset();
        RunConfigStore.Reset();
    }

    private static Envelope Cmd(string value, params string[] args) =>
        new(EnvelopeType.Command, value, args);

    private static void Plan() =>
        DevelopmentTasks.Plan(Cmd("plan", FeaturesJson, "dotnet test", "src/app"));

    /// <summary>Leva o flow até deixar uma feature escolhida e implementada (pronta p/ verify).</summary>
    private static void AdvanceToVerify()
    {
        Plan();
        DevelopmentTasks.Bearings(Cmd("bearings", "orientado"));
        DevelopmentTasks.Smoke(Cmd("smoke", "baseline ok"));
        DevelopmentTasks.Pick(Cmd("pick"));
        DevelopmentTasks.Implement(Cmd("implement", "implementei"));
    }

    [Fact]
    public void Start_SemFeaturePendente_ResetaFeatureListERunConfig()
    {
        // Um run anterior terminou (tudo passando) - "start" pode começar de verdade do zero.
        Plan();
        foreach (var f in FeatureStore.Load())
            FeatureStore.MarkPassed(f.Id);
        Assert.NotEmpty(FeatureStore.Load());

        DevelopmentTasks.Start();

        Assert.Empty(FeatureStore.Load());
        Assert.Equal(new RunConfig(), RunConfigStore.Load());
    }

    [Fact]
    public void Start_ComFeaturePendente_RetomaViaBearingsEmVezDeResetar()
    {
        // Uma sessão anterior (talvez outro driver) morreu no meio da feature "B" (id 2, ainda
        // pendente). "start" não pode apagar nada - deve rotear direto para bearings.
        AdvanceToVerify(); // ...→ implement, sessão "morre" aqui, antes do verify

        // TaskRegistry.Dispatch sempre reseta state.json incondicionalmente antes de chamar o
        // Start() do domínio - reproduz isso aqui, já que este teste chama Start() diretamente.
        StateStore.Reset();

        var result = DevelopmentTasks.Start();

        Assert.Contains("NOVA SESSÃO", result); // bearings_prompt, não o inicializador
        Assert.Equal(2, FeatureStore.Load().Count); // intacta
        Assert.Equal(2, FeatureStore.PendingCount()); // nenhuma marcada como passando
        Assert.Equal("dotnet test", RunConfigStore.Load().VerifyCmd); // intacto
        Assert.Equal("src/app", RunConfigStore.Load().TargetDir);
    }

    [Fact]
    public void Plan_PersisteFeaturesERoteiaParaBearings()
    {
        var result = DevelopmentTasks.Plan(Cmd("plan", FeaturesJson, "npm test", "web"));

        Assert.Equal(2, FeatureStore.Load().Count);
        Assert.Equal("npm test", RunConfigStore.Load().VerifyCmd);
        Assert.Equal("web", RunConfigStore.Load().TargetDir);
        Assert.Contains("NOVA SESSÃO", result);
        Assert.Contains("\"value\":\"bearings\"", result);
    }

    [Fact]
    public void Plan_FeaturesInvalidas_ReemiteOPlano()
    {
        var result = DevelopmentTasks.Plan(Cmd("plan", "não é json", "dotnet test", "."));

        Assert.Empty(FeatureStore.Load());
        Assert.Equal(new RunConfig(), RunConfigStore.Load()); // nada persistido
        Assert.Contains("\"value\":\"plan\"", result);
        Assert.DoesNotContain("NOVA SESSÃO", result);
    }

    [Fact]
    public void Pick_EscolheMaiorPrioridadeEGravaAFeatureCorrente()
    {
        Plan();
        DevelopmentTasks.Bearings(Cmd("bearings", "ok"));
        var afterSmoke = DevelopmentTasks.Smoke(Cmd("smoke", "ok"));
        Assert.Contains("\"value\":\"pick\"", afterSmoke);

        var implement = DevelopmentTasks.Pick(Cmd("pick"));

        Assert.Equal("2", StateStore.Get("current_feature_id")); // prioridade 1 = id 2 ("B")
        Assert.Contains("B", implement);
        Assert.Contains("\"value\":\"implement\"", implement);
    }

    [Fact]
    public void Verify_Fail_VoltaParaImplement()
    {
        AdvanceToVerify();

        var result = DevelopmentTasks.Verify(Cmd("verify", "FAIL: testes vermelhos"));

        Assert.Contains("FALHOU", result);
        Assert.Contains("\"value\":\"implement\"", result);
    }

    [Fact]
    public void Verify_Pass_SegueParaHandoff()
    {
        AdvanceToVerify();

        var result = DevelopmentTasks.Verify(Cmd("verify", "PASS"));

        Assert.Contains("\"value\":\"handoff\"", result);
    }

    [Fact]
    public void Verify_VereditoInvalido_ReemiteVerify()
    {
        AdvanceToVerify();

        var result = DevelopmentTasks.Verify(Cmd("verify", "rodei os testes e passou"));

        Assert.Contains("\"value\":\"verify\"", result);
        Assert.DoesNotContain("\"value\":\"handoff\"", result);
        Assert.Contains("não começou", result);
    }

    [Fact]
    public void Handoff_Vazio_ReemiteHandoffENaoMarcaFeatureComoPassando()
    {
        AdvanceToVerify();
        DevelopmentTasks.Verify(Cmd("verify", "PASS"));

        var result = DevelopmentTasks.Handoff(Cmd("handoff", ""));

        Assert.Contains("\"value\":\"handoff\"", result);
        Assert.Equal(2, FeatureStore.PendingCount());
    }

    [Fact]
    public void Handoff_ComPendencia_AbreNovaSessao_ComTudoPassando_Encerra()
    {
        // 1ª feature (id 2)
        AdvanceToVerify();
        DevelopmentTasks.Verify(Cmd("verify", "PASS"));
        var afterFirst = DevelopmentTasks.Handoff(Cmd("handoff", "abc123"));

        Assert.Contains("NOVA SESSÃO", afterFirst); // ainda falta a id 1
        Assert.Equal(1, FeatureStore.PendingCount());

        // 2ª feature (id 1)
        DevelopmentTasks.Bearings(Cmd("bearings", "ok"));
        DevelopmentTasks.Smoke(Cmd("smoke", "ok"));
        DevelopmentTasks.Pick(Cmd("pick"));
        DevelopmentTasks.Implement(Cmd("implement", "feito"));
        DevelopmentTasks.Verify(Cmd("verify", "PASS"));
        var afterSecond = DevelopmentTasks.Handoff(Cmd("handoff", "def456"));

        Assert.Equal("stop", afterSecond);
        Assert.True(FeatureStore.AllPassing());
    }

    [Fact]
    public void GuardaPorFeature_AoExcederOTeto_Encerra()
    {
        Plan();
        DevelopmentTasks.Bearings(Cmd("bearings", "ok")); // zera para 1
        StateStore.Set("feature_steps", DevelopmentTasks.StepsPerFeature.ToString()); // no limite

        var result = DevelopmentTasks.Smoke(Cmd("smoke", "ok")); // próximo bump ultrapassa

        Assert.Equal("stop", result);
    }

    [Fact]
    public void Plan_DependsOnCiclico_ReemiteOPlano()
    {
        var result = DevelopmentTasks.Plan(Cmd("plan",
            """[{"id":1,"title":"A","priority":1,"dependsOn":[2]},{"id":2,"title":"B","priority":2,"dependsOn":[1]}]""",
            "dotnet test", "."));

        Assert.Empty(FeatureStore.Load());
        Assert.Equal(new RunConfig(), RunConfigStore.Load());
        Assert.Contains("\"value\":\"plan\"", result);
        Assert.DoesNotContain("NOVA SESSÃO", result);
    }

    [Fact]
    public void Plan_DependsOnIdInexistente_ReemiteOPlano()
    {
        var result = DevelopmentTasks.Plan(Cmd("plan",
            """[{"id":1,"title":"A","priority":1,"dependsOn":[99]}]""",
            "dotnet test", "."));

        Assert.Empty(FeatureStore.Load());
        Assert.Contains("\"value\":\"plan\"", result);
        Assert.DoesNotContain("NOVA SESSÃO", result);
    }

    [Fact]
    public void Plan_CorteMaxFeatures_RemoveDependenciaParaIdCortado()
    {
        // id 1 (prioridade 1, a melhor) sobrevive ao corte; depende do id 2, cuja prioridade
        // (1000) é a pior de todas — garantidamente cortado pelo Take(MaxFeatures). Os "extras"
        // preenchem as vagas restantes com prioridades intermediárias.
        var extrasJson = string.Join(",", Enumerable.Range(3, DevelopmentTasks.MaxFeatures - 1)
            .Select(i => $$"""{"id":{{i}},"title":"extra{{i}}","priority":{{i}}}"""));
        var json = """[{"id":1,"title":"sobrevivente","priority":1,"dependsOn":[2]},{"id":2,"title":"cortada","priority":1000},"""
            + extrasJson + "]";

        DevelopmentTasks.Plan(Cmd("plan", json, "dotnet test", "."));

        Assert.DoesNotContain(2, FeatureStore.Load().Select(f => f.Id)); // id 2 foi de fato cortado
        var survivor = FeatureStore.Load().Single(f => f.Id == 1);
        Assert.DoesNotContain(2, survivor.Deps); // ...e a dependência não pode sobrar
    }

    [Fact]
    public void Pick_RespeitaDependencia_EscolheDependenciaAntesDaDependente()
    {
        // f1: prioridade pior, sem deps. f2: prioridade melhor, mas depende de f1.
        var json = """[{"id":1,"title":"fundação","priority":2},{"id":2,"title":"depende","priority":1,"dependsOn":[1]}]""";
        DevelopmentTasks.Plan(Cmd("plan", json, "dotnet test", "."));
        DevelopmentTasks.Bearings(Cmd("bearings", "ok"));
        DevelopmentTasks.Smoke(Cmd("smoke", "ok"));

        DevelopmentTasks.Pick(Cmd("pick"));

        Assert.Equal("1", StateStore.Get("current_feature_id"));
    }

    [Fact]
    public void Pick_SemFeatureProntaMasComPendencia_EncerraSemReportarConcluido()
    {
        // Grafo bloqueado gravado direto via Write (bypassando a validação de Parse).
        Plan(); // popula RunConfig; a lista será sobrescrita a seguir
        FeatureStore.Write([
            new Feature(1, "A", 1, false, [2]),
            new Feature(2, "B", 2, false, [1]),
        ]);
        DevelopmentTasks.Bearings(Cmd("bearings", "ok"));
        DevelopmentTasks.Smoke(Cmd("smoke", "ok"));

        var result = DevelopmentTasks.Pick(Cmd("pick"));

        Assert.Equal("stop", result);
        Assert.Equal(2, FeatureStore.PendingCount()); // nada foi marcado como passando
    }
}
