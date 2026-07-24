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

    private readonly string _targetDir;

    public DevelopmentFlowTests()
    {
        Clean();
        _targetDir = Path.Combine(Path.GetTempPath(), "development-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_targetDir);
    }

    public void Dispose()
    {
        Clean();
        if (Directory.Exists(_targetDir))
            Directory.Delete(_targetDir, recursive: true);
    }

    private static void Clean()
    {
        StateStore.Reset();
        Trace.Reset();
        FeatureStore.Reset();
        RunConfigStore.Reset();
    }

    // Espelha a fiação real de Flows.Development/Program.cs: só reseta StateStore/Trace no
    // "start" quando não há feature pendente — sessão fresca do hard reset por feature deve
    // RETOMAR, não apagar a trajetória/step acumulados das features anteriores.
    private static readonly Dictionary<string, Func<Envelope?, string>> DispatchTasks = new()
    {
        ["start"] = _ => DevelopmentTasks.Start(),
        ["plan"] = e => DevelopmentTasks.Plan(e),
        ["bearings"] = e => DevelopmentTasks.Bearings(e),
        ["smoke"] = e => DevelopmentTasks.Smoke(e),
        ["pick"] = e => DevelopmentTasks.Pick(e),
        ["implement"] = e => DevelopmentTasks.Implement(e),
    };

    private static string DispatchJson(string json) =>
        TaskRegistry.Dispatch([json], DispatchTasks, shouldResetOnStart: () => FeatureStore.PendingCount() == 0);

    private static Envelope Cmd(string value, params string[] args) =>
        new(EnvelopeType.Command, value, args);

    private static string Git(string workingDirectory, params string[] args)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "git";
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}{stdout}");
        return stdout;
    }

    private void Plan() =>
        DevelopmentTasks.Plan(Cmd("plan", FeaturesJson, "dotnet test", _targetDir));

    /// <summary>Leva o flow até deixar uma feature escolhida e implementada (pronta p/ verify).</summary>
    private void AdvanceToVerify()
    {
        Plan();
        DevelopmentTasks.Bearings(Cmd("bearings", "orientado"));
        DevelopmentTasks.Smoke(Cmd("smoke", "baseline ok"));
        DevelopmentTasks.Pick(Cmd("pick"));
        DevelopmentTasks.Implement(Cmd("implement", "implementei"));
    }

    private static void WriteVerifyFeatureScript(string targetDir, string body)
    {
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "verify-feature.sh"), body.Replace("\r\n", "\n"));
    }

    private static string VerifyLogPath(int featureId) =>
        Path.Combine(".harness", "logs", $"verify-feature-{featureId}.log");

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

        var result = DevelopmentTasks.Start();

        Assert.Contains("NOVA SESSÃO", result); // bearings_prompt, não o inicializador
        Assert.Equal(2, FeatureStore.Load().Count); // intacta
        Assert.Equal(2, FeatureStore.PendingCount()); // nenhuma marcada como passando
        Assert.Equal("dotnet test", RunConfigStore.Load().VerifyCmd); // intacto
        Assert.Equal(_targetDir, RunConfigStore.Load().TargetDir);
    }

    [Fact]
    public void Dispatch_StartComFeaturePendente_NaoTruncaTraceNemStep()
    {
        // Reproduz o hard reset por feature: uma feature ainda pendente ("B") e um trace/step
        // já acumulados por features anteriores, quando a sessão fresca reabre com "start".
        AdvanceToVerify(); // deixa a feature "B" pendente, sessão "morre" antes do verify
        Trace.Append(41, "handoff", TraceOutcome.Instruction, 10); // trajetória de features passadas
        var stepAntesDoStart = StateStore.Load().Step;

        var result = DispatchJson("""{"type":"text","value":"start"}""");

        Assert.Contains("NOVA SESSÃO", result); // retomou via bearings, não reiniciou
        Assert.Contains(Trace.Load(), e => e is { Step: 41, Command: "handoff" }); // trace preservado
        Assert.Equal(stepAntesDoStart + 1, StateStore.Load().Step); // contador continuou, não voltou a 1
    }

    [Fact]
    public void Dispatch_StartSemFeaturePendente_TruncaTraceEStep()
    {
        // Sem run em andamento, "start" É um início de verdade e deve truncar trace/step.
        Plan();
        foreach (var f in FeatureStore.Load())
            FeatureStore.MarkPassed(f.Id);
        Trace.Append(41, "handoff", TraceOutcome.Instruction, 10);

        DispatchJson("""{"type":"text","value":"start"}""");

        Assert.DoesNotContain(Trace.Load(), e => e.Step == 41);
        Assert.Equal(1, StateStore.Load().Step);
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
    public void Verify_Pass_ExecutaHandoffAutomaticoEAvanca()
    {
        AdvanceToVerify();

        var result = DevelopmentTasks.Verify(Cmd("verify", "PASS"));

        Assert.Contains("NOVA SESSÃO", result); // ainda falta a id 1
        Assert.DoesNotContain("\"value\":\"handoff\"", result);
        Assert.Equal(1, FeatureStore.PendingCount());
        Assert.Contains("Feature #2", File.ReadAllText(Path.Combine(_targetDir, "progress.txt")));
    }

    [Fact]
    public void Implement_ComVerifyFeaturePassando_ExecutaVerifyEHandoffAutomaticos()
    {
        WriteVerifyFeatureScript(_targetDir,
            """
            #!/usr/bin/env bash
            set -euo pipefail
            echo "PASS: feature $1 verificada"
            """);
        Plan();
        DevelopmentTasks.Bearings(Cmd("bearings", "orientado"));
        DevelopmentTasks.Smoke(Cmd("smoke", "baseline ok"));
        DevelopmentTasks.Pick(Cmd("pick"));

        var result = DevelopmentTasks.Implement(Cmd("implement", "implementei"));

        Assert.Contains("NOVA SESSÃO", result);
        Assert.DoesNotContain("\"value\":\"verify\"", result);
        Assert.Equal(1, FeatureStore.PendingCount());
        var progress = File.ReadAllText(Path.Combine(_targetDir, "progress.txt"));
        Assert.Contains("Feature #2", progress);
        Assert.Contains("PASS: feature 2 verificada", progress);
        Assert.Contains(".harness/logs/verify-feature-2.log", progress);
        Assert.Contains("command: bash ./verify-feature.sh 2", File.ReadAllText(VerifyLogPath(2)));
    }

    [Fact]
    public void Implement_ComVerifyFeatureFalhando_VoltaParaFix()
    {
        WriteVerifyFeatureScript(_targetDir,
            """
            #!/usr/bin/env bash
            set -euo pipefail
            echo "FAIL: feature $1 quebrou"
            echo "LINHA DETALHADA QUE FICA SO NO LOG"
            exit 7
            """);
        Plan();
        DevelopmentTasks.Bearings(Cmd("bearings", "orientado"));
        DevelopmentTasks.Smoke(Cmd("smoke", "baseline ok"));
        DevelopmentTasks.Pick(Cmd("pick"));

        var result = DevelopmentTasks.Implement(Cmd("implement", "implementei"));

        Assert.Contains("FALHOU", result);
        Assert.Contains("feature 2 quebrou", result);
        Assert.Contains(".harness/logs/verify-feature-2.log", result);
        Assert.DoesNotContain("LINHA DETALHADA QUE FICA SO NO LOG", result);
        var log = File.ReadAllText(VerifyLogPath(2));
        Assert.Contains("FAIL: feature 2 quebrou", log);
        Assert.Contains("LINHA DETALHADA QUE FICA SO NO LOG", log);
        Assert.Contains("\"value\":\"implement\"", result);
        Assert.Equal(2, FeatureStore.PendingCount());
        Assert.False(File.Exists(Path.Combine(_targetDir, "progress.txt")));
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

        var result = DevelopmentTasks.Handoff(Cmd("handoff", ""));

        Assert.Contains("\"value\":\"handoff\"", result);
        Assert.Equal(2, FeatureStore.PendingCount());
    }

    [Fact]
    public void Handoff_ComPendencia_AbreNovaSessao_ComTudoPassando_Encerra()
    {
        // 1ª feature (id 2)
        AdvanceToVerify();
        var afterFirst = DevelopmentTasks.Verify(Cmd("verify", "PASS"));

        Assert.Contains("NOVA SESSÃO", afterFirst); // ainda falta a id 1
        Assert.Equal(1, FeatureStore.PendingCount());

        // 2ª feature (id 1)
        DevelopmentTasks.Bearings(Cmd("bearings", "ok"));
        DevelopmentTasks.Smoke(Cmd("smoke", "ok"));
        DevelopmentTasks.Pick(Cmd("pick"));
        DevelopmentTasks.Implement(Cmd("implement", "feito"));
        var afterSecond = DevelopmentTasks.Verify(Cmd("verify", "PASS"));

        Assert.Equal("stop", afterSecond);
        Assert.True(FeatureStore.AllPassing());
    }

    [Fact]
    public void Handoff_LegadoComHash_MarcaFeatureComoPassando()
    {
        AdvanceToVerify();

        var result = DevelopmentTasks.Handoff(Cmd("handoff", "abc123"));

        Assert.Contains("NOVA SESSÃO", result);
        Assert.Equal(1, FeatureStore.PendingCount());
    }

    [Fact]
    public void Verify_Pass_HandoffAutomaticoCommitaSoODiretorioAlvo()
    {
        var repo = Path.Combine(_targetDir, "repo");
        var target = Path.Combine(repo, "app");
        Directory.CreateDirectory(target);
        Git(repo, "init");
        Git(repo, "config", "user.email", "harness@example.test");
        Git(repo, "config", "user.name", "Harness Test");

        File.WriteAllText(Path.Combine(repo, "outside.txt"), "fora do target");

        DevelopmentTasks.Plan(Cmd("plan", FeaturesJson, "dotnet test", target));
        DevelopmentTasks.Bearings(Cmd("bearings", "ok"));
        DevelopmentTasks.Smoke(Cmd("smoke", "ok"));
        DevelopmentTasks.Pick(Cmd("pick"));
        DevelopmentTasks.Implement(Cmd("implement", "feito no target"));

        var result = DevelopmentTasks.Verify(Cmd("verify", "PASS: tudo verde"));

        Assert.Contains("NOVA SESSÃO", result);
        var committedFiles = Git(repo, "show", "--name-only", "--format=", "HEAD");
        Assert.Contains("app/progress.txt", committedFiles);
        Assert.DoesNotContain("outside.txt", committedFiles);
        Assert.Contains("?? outside.txt", Git(repo, "status", "--short"));
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
