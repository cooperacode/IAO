using Flows.Development;
using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// The development flow's per-feature loop: each task decides the NEXT command
/// (evaluation-gate pattern). Covers the branches — verify FAIL↺implement, verify
/// PASS→handoff, handoff→bearings (next feature) vs. stop — and the per-feature guard.
/// </summary>
public class DevelopmentFlowTests : IDisposable
{
    // id 1 has priority 2; id 2 has priority 1 → the highest-priority one is id 2.
    private const string FeaturesJson =
        """[{"id":1,"title":"A","priority":2},{"id":2,"title":"B","priority":1}]""";

    // "specs" folder relative to the test process's CWD (the same one HarnessConfig.DocsFolder
    // uses by default) — only the brief tests populate it; created/deleted by them.
    private static readonly string SpecsDir = Path.Combine(Directory.GetCurrentDirectory(), "specs");

    private readonly string _targetDir;

    public DevelopmentFlowTests()
    {
        Clean();
        _targetDir = Path.Combine(Path.GetTempPath(), "development-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_targetDir);
        WriteInitScript(_targetDir);
    }

    public void Dispose()
    {
        Clean();
        if (Directory.Exists(_targetDir))
            Directory.Delete(_targetDir, recursive: true);
        if (Directory.Exists(SpecsDir))
            Directory.Delete(SpecsDir, recursive: true);
    }

    // Mirrors DevelopmentTasks.PlanFilePath (private) — the file the driver writes the raw
    // feature-list JSON array to, instead of embedding it in the envelope's args.
    private const string PlanFilePath = ".harness/plan.json";

    private static void Clean()
    {
        StateStore.Reset();
        Trace.Reset();
        FeatureStore.Reset();
        RunConfigStore.Reset();
        ArtifactStore.Reset();
        if (File.Exists(PlanFilePath))
            File.Delete(PlanFilePath);
    }

    private static void WritePlanFile(string features)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PlanFilePath)!);
        File.WriteAllText(PlanFilePath, features);
    }

    private static Envelope PlanCmd(string features, string verifyCmd, string targetDir)
    {
        WritePlanFile(features);
        return Cmd("plan", verifyCmd, targetDir);
    }

    private static void GivenDocsBrief(string content)
    {
        Directory.CreateDirectory(SpecsDir);
        File.WriteAllText(Path.Combine(SpecsDir, "brief.md"), content);
    }

    // Mirrors Flows.Development/Program.cs's real wiring: only resets StateStore/Trace on
    // "start" when there's no pending feature — a fresh session from the per-feature hard
    // reset must RESUME, not erase the trajectory/step accumulated by previous features.
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

    private string Plan() =>
        DevelopmentTasks.Plan(PlanCmd(FeaturesJson, "dotnet test", _targetDir));

    /// <summary>Advances the flow until a feature is chosen and implemented (ready for verify).</summary>
    private void AdvanceToVerify()
    {
        Plan();
        DevelopmentTasks.Bearings(Cmd("bearings", "orientado"));
        DevelopmentTasks.Implement(Cmd("implement", "implementei"));
    }

    private static void WriteInitScript(string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "init.sh"),
            "#!/usr/bin/env bash\nset -euo pipefail\n");
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
        // A previous run finished (everything passing) - "start" can genuinely begin from scratch.
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
        // A previous session (maybe another driver) died mid-feature "B" (id 2, still
        // pending). "start" must not erase anything - it should route straight to bearings.
        AdvanceToVerify(); // ...→ implement, session "dies" here, before verify

        var result = DevelopmentTasks.Start();

        Assert.Contains("\"value\":\"implement\"", result); // deterministic session setup
        Assert.Equal(2, FeatureStore.Load().Count); // untouched
        Assert.Equal(2, FeatureStore.PendingCount()); // none marked as passing
        Assert.Equal("dotnet test", RunConfigStore.Load().VerifyCmd); // untouched
        Assert.Equal(_targetDir, RunConfigStore.Load().TargetDir);
    }

    [Fact]
    public void Dispatch_StartComFeaturePendente_NaoTruncaTraceNemStep()
    {
        // Reproduces the per-feature hard reset: a feature still pending ("B") and a
        // trace/step already accumulated by previous features, when the fresh session
        // reopens with "start".
        AdvanceToVerify(); // leaves feature "B" pending, session "dies" before verify
        Trace.Append(41, "handoff", TraceOutcome.Instruction, 10); // trajectory from past features
        var stepBeforeStart = StateStore.Load().Step;

        var result = DispatchJson("""{"type":"text","value":"start"}""");

        Assert.Contains("\"value\":\"implement\"", result); // resumed without restart
        Assert.Contains(Trace.Load(), e => e is { Step: 41, Command: "handoff" }); // trace preserved
        Assert.Equal(stepBeforeStart + 1, StateStore.Load().Step); // counter continued, didn't go back to 1
    }

    [Fact]
    public void Dispatch_StartSemFeaturePendente_TruncaTraceEStep()
    {
        // With no run in progress, "start" IS a genuine beginning and must truncate trace/step.
        Plan();
        foreach (var f in FeatureStore.Load())
            FeatureStore.MarkPassed(f.Id);
        Trace.Append(41, "handoff", TraceOutcome.Instruction, 10);

        DispatchJson("""{"type":"text","value":"start"}""");

        Assert.DoesNotContain(Trace.Load(), e => e.Step == 41);
        Assert.Equal(1, StateStore.Load().Step);
    }

    [Fact]
    public void Plan_PersisteFeaturesERoteiaDiretoParaImplementacao()
    {
        var result = DevelopmentTasks.Plan(PlanCmd(FeaturesJson, "npm test", _targetDir));

        Assert.Equal(2, FeatureStore.Load().Count);
        Assert.Equal("npm test", RunConfigStore.Load().VerifyCmd);
        Assert.Equal(_targetDir, RunConfigStore.Load().TargetDir);
        Assert.Contains("\"value\":\"implement\"", result);
    }

    [Fact]
    public void Plan_GeraUmRunIdNovoENaoVazio()
    {
        DevelopmentTasks.Plan(PlanCmd(FeaturesJson, "npm test", "web"));

        var runId = RunConfigStore.Load().RunId;

        Assert.False(string.IsNullOrWhiteSpace(runId));
        Assert.True(Guid.TryParse(runId, out _));
    }

    [Fact]
    public void Start_ComFeaturePendente_PreservaORunIdDoPlanAnterior()
    {
        AdvanceToVerify(); // ...→ implement, session "dies" here, before verify
        var runIdBeforeStart = RunConfigStore.Load().RunId;
        Assert.False(string.IsNullOrWhiteSpace(runIdBeforeStart));

        DevelopmentTasks.Start();

        // Resuming doesn't generate a new run - run identity has to survive "start".
        Assert.Equal(runIdBeforeStart, RunConfigStore.Load().RunId);
    }

    // --- brief: persistence in Start() and reinjection in implement -----------------------

    [Fact]
    public void Start_ComDocsPopulados_PersisteOBriefNoArtifactStore()
    {
        GivenDocsBrief("# Brief\n\nBuild a task-management app.");

        DevelopmentTasks.Start();

        // DocsReader.Read prepends a "## <file>" heading before the content — Contains, not
        // exact equality (same pattern as DocsReaderTests for the consolidated content).
        Assert.Contains("Build a task-management app.", ArtifactStore.Read("brief"));
    }

    [Fact]
    public void Start_ModoInterativo_NaoPersisteBrief()
    {
        DevelopmentTasks.Start(); // sem specs/ → InitializerInteractive()

        Assert.Equal("", ArtifactStore.Read("brief"));
    }

    [Fact]
    public void Start_NovoRunSemDocs_ApagaBriefDoRunAnterior()
    {
        // A second run with the SAME specs/ would already self-correct via overwrite (it
        // doesn't prove anything about Reset()); the case only ArtifactStore.Reset() solves
        // is specs→interactive: interactive mode never calls Write, so without Reset() the
        // old brief would leak through.
        GivenDocsBrief("topic A brief");
        DevelopmentTasks.Start();
        Plan();
        foreach (var f in FeatureStore.Load())
            FeatureStore.MarkPassed(f.Id);
        Directory.Delete(SpecsDir, recursive: true);

        DevelopmentTasks.Start(); // run novo, sem specs/ → interativo

        Assert.Equal("", ArtifactStore.Read("brief"));
    }

    [Fact]
    public void Plan_RetornaImplementSemReinjetarOBrief()
    {
        GivenDocsBrief("topic A brief");
        DevelopmentTasks.Start();

        var result = Plan();

        Assert.DoesNotContain("topic A brief", result);
    }

    [Fact]
    public void BearingsSelecionaImplementSemReinjetarOBrief()
    {
        GivenDocsBrief("topic A brief");
        DevelopmentTasks.Start();
        Plan();

        var result = DevelopmentTasks.Bearings(Cmd("bearings", "ok"));

        Assert.DoesNotContain("topic A brief", result);
    }

    [Fact]
    public void BearingsEImplement_SemBriefPersistido_NaoTemTagBrief()
    {
        // No specs/: interactive mode, no persisted brief — the block disappears, not empty.
        var implement = Plan();

        Assert.DoesNotContain("<brief>", implement);
    }

    [Fact]
    public void ImplementPrompt_ComDescriptionEReferencesDaFeature()
    {
        const string json =
            """[{"id":1,"title":"A","priority":2,"description":"faz X","references":["RF-003"],"implementationContext":{"requirements":["inline X"]}},{"id":2,"title":"B","priority":1}]""";
        WriteVerifyFeatureScript(_targetDir,
            "#!/usr/bin/env bash\nset -euo pipefail\necho \"PASS: feature $1 verificada\"\n");
        DevelopmentTasks.Plan(PlanCmd(json, "dotnet test", _targetDir));
        DevelopmentTasks.Bearings(Cmd("bearings", "ok")); // escolhe "B"
        var result = DevelopmentTasks.Implement(Cmd("implement", "feito")); // completes B, auto-advances to A

        Assert.Contains("Description: faz X", result);
        Assert.Contains("Brief references: RF-003", result);
        Assert.Contains("<implementation-context>requirements: inline X", result);
        Assert.DoesNotContain("<brief>", result);
    }

    [Fact]
    public void ImplementPrompt_SemDescriptionNemReferences_NaoTemBlocoDeContexto()
    {
        Plan(); // FeaturesJson sem description/references
        var result = DevelopmentTasks.Bearings(Cmd("bearings", "ok"));

        Assert.DoesNotContain("Description:", result);
        Assert.DoesNotContain("Brief references:", result);
    }

    [Fact]
    public void Plan_FeaturesInvalidas_ReemiteOPlano()
    {
        var result = DevelopmentTasks.Plan(PlanCmd("not json", "dotnet test", "."));

        Assert.Empty(FeatureStore.Load());
        Assert.Equal(new RunConfig(), RunConfigStore.Load()); // nada persistido
        Assert.Contains("\"value\":\"plan\"", result);
        Assert.DoesNotContain("NEW SESSION", result);
    }

    [Fact]
    public void Pick_EscolheMaiorPrioridadeEGravaAFeatureCorrente()
    {
        Plan();
        var implement = DevelopmentTasks.Bearings(Cmd("bearings", "ok"));

        Assert.Equal("2", StateStore.Get("current_feature_id")); // prioridade 1 = id 2 ("B")
        Assert.Contains("B", implement);
        Assert.Contains("\"value\":\"implement\"", implement);
        Assert.Contains("=== NEW SESSION (clean context) ===", implement);
    }

    [Fact]
    public void Verify_Fail_VoltaParaImplement()
    {
        AdvanceToVerify();

        var result = DevelopmentTasks.Verify(Cmd("verify", "FAIL: testes vermelhos"));

        Assert.Contains("FAILED", result);
        Assert.Contains("\"value\":\"implement\"", result);
        Assert.DoesNotContain("NEW SESSION", result);
    }

    [Fact]
    public void Verify_Pass_ExecutaHandoffAutomaticoEAvanca()
    {
        WriteVerifyFeatureScript(_targetDir,
            "#!/usr/bin/env bash\nset -euo pipefail\necho \"PASS: feature $1 verificada\"\n");
        Plan();
        var result = DevelopmentTasks.Implement(Cmd("implement", "feito"));

        Assert.Contains("\"value\":\"implement\"", result); // ainda falta a id 1
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

        var result = DevelopmentTasks.Implement(Cmd("implement", "implementei"));

        Assert.Contains("\"value\":\"implement\"", result);
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

        var result = DevelopmentTasks.Implement(Cmd("implement", "implementei"));

        Assert.Contains("FAILED", result);
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
    public void Smoke_IgnoraConfirmacaoTextualEReexecutaInitSh()
    {
        File.WriteAllText(Path.Combine(_targetDir, "init.sh"),
            "#!/usr/bin/env bash\nset -euo pipefail\necho falhou\nexit 9\n");
        Plan();

        var result = DevelopmentTasks.Smoke(Cmd("smoke", "ok"));

        Assert.Contains("smoke", result);
        Assert.DoesNotContain("\"value\":\"implement\"", result);
        Assert.Contains("exitCode: 9", File.ReadAllText(Path.Combine(".harness", "logs", "smoke.log")));
    }

    [Fact]
    public void Implement_SemScriptExecutaVerifyCmdDeterministicamente()
    {
        DevelopmentTasks.Plan(PlanCmd(FeaturesJson, "true", _targetDir));

        var result = DevelopmentTasks.Implement(Cmd("implement", "feito"));

        Assert.Contains("\"value\":\"implement\"", result);
        Assert.Equal(1, FeatureStore.PendingCount());
        Assert.Contains("command: true", File.ReadAllText(VerifyLogPath(2)));
    }

    [Fact]
    public void VerifyCmd_ComOperadorDeShellNaoEhExecutado()
    {
        DevelopmentTasks.Plan(PlanCmd(FeaturesJson, "true && false", _targetDir));

        var result = DevelopmentTasks.Implement(Cmd("implement", "feito"));

        Assert.Contains("no deterministic verify command", result);
        Assert.Contains("\"value\":\"implement\"", result);
        Assert.Equal(2, FeatureStore.PendingCount());
    }

    [Fact]
    public void Verify_IgnoresVereditoTextualEExecutaComandoDeterministico()
    {
        AdvanceToVerify();

        var result = DevelopmentTasks.Verify(Cmd("verify", "rodei os testes e passou"));

        Assert.Contains("FAILED", result);
        Assert.Contains("\"value\":\"implement\"", result);
    }

    [Fact]
    public void Handoff_SemPassDeterministico_RetornaParaVerify()
    {
        AdvanceToVerify();

        var result = DevelopmentTasks.Handoff(Cmd("handoff", ""));

        Assert.Contains("\"value\":\"verify\"", result);
        Assert.Equal(2, FeatureStore.PendingCount());
    }

    [Fact]
    public void Handoff_ComPendencia_AbreNovaSessao_ComTudoPassando_Encerra()
    {
        // 1ª feature (id 2)
        WriteVerifyFeatureScript(_targetDir,
            "#!/usr/bin/env bash\nset -euo pipefail\necho \"PASS: feature $1 verificada\"\n");
        Plan();
        var afterFirst = DevelopmentTasks.Implement(Cmd("implement", "feito"));

        Assert.Contains("\"value\":\"implement\"", afterFirst); // ainda falta a id 1
        Assert.Equal(1, FeatureStore.PendingCount());

        // 2ª feature (id 1)
        var afterSecond = DevelopmentTasks.Implement(Cmd("implement", "feito"));

        Assert.Equal("stop", afterSecond);
        Assert.True(FeatureStore.AllPassing());
    }

    [Fact]
    public void Handoff_HashTextualNaoSubstituiVerifyDeterministico()
    {
        AdvanceToVerify();

        var result = DevelopmentTasks.Handoff(Cmd("handoff", "abc123"));

        Assert.Contains("\"value\":\"verify\"", result);
        Assert.Equal(2, FeatureStore.PendingCount());
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

        DevelopmentTasks.Plan(PlanCmd(FeaturesJson, "dotnet test", target));
        WriteInitScript(target);
        WriteVerifyFeatureScript(target,
            "#!/usr/bin/env bash\nset -euo pipefail\necho \"PASS: feature $1 verificada\"\n");
        DevelopmentTasks.Bearings(Cmd("bearings", "ok"));
        var result = DevelopmentTasks.Implement(Cmd("implement", "feito no target"));

        Assert.Contains("\"value\":\"implement\"", result);
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

        var result = DevelopmentTasks.Smoke(Cmd("smoke", "ok")); // next bump goes over

        Assert.Equal("stop", result);
    }

    [Fact]
    public void Plan_DependsOnCiclico_ReemiteOPlano()
    {
        var result = DevelopmentTasks.Plan(PlanCmd(
            """[{"id":1,"title":"A","priority":1,"dependsOn":[2]},{"id":2,"title":"B","priority":2,"dependsOn":[1]}]""",
            "dotnet test", "."));

        Assert.Empty(FeatureStore.Load());
        Assert.Equal(new RunConfig(), RunConfigStore.Load());
        Assert.Contains("\"value\":\"plan\"", result);
        Assert.DoesNotContain("NEW SESSION", result);
    }

    [Fact]
    public void Plan_DependsOnIdInexistente_ReemiteOPlano()
    {
        var result = DevelopmentTasks.Plan(PlanCmd(
            """[{"id":1,"title":"A","priority":1,"dependsOn":[99]}]""",
            "dotnet test", "."));

        Assert.Empty(FeatureStore.Load());
        Assert.Contains("\"value\":\"plan\"", result);
        Assert.DoesNotContain("NEW SESSION", result);
    }

    [Fact]
    public void Plan_CorteMaxFeatures_RemoveDependenciaParaIdCortado()
    {
        // id 1 (prioridade 1, a melhor) sobrevive ao corte; depende do id 2, cuja prioridade
        // (1000) is the worst of all — guaranteed to be cut by Take(MaxFeatures). The "extras"
        // fill the remaining slots with intermediate priorities.
        var extrasJson = string.Join(",", Enumerable.Range(3, DevelopmentTasks.MaxFeatures - 1)
            .Select(i => $$"""{"id":{{i}},"title":"extra{{i}}","priority":{{i}}}"""));
        var json = """[{"id":1,"title":"sobrevivente","priority":1,"dependsOn":[2]},{"id":2,"title":"cortada","priority":1000},"""
            + extrasJson + "]";

        DevelopmentTasks.Plan(PlanCmd(json, "dotnet test", _targetDir));

        Assert.DoesNotContain(2, FeatureStore.Load().Select(f => f.Id)); // id 2 foi de fato cortado
        var survivor = FeatureStore.Load().Single(f => f.Id == 1);
        Assert.DoesNotContain(2, survivor.Deps); // ...and the dependency can't survive either
    }

    [Fact]
    public void Pick_RespeitaDependencia_EscolheDependenciaAntesDaDependente()
    {
        // f1: prioridade pior, sem deps. f2: prioridade melhor, mas depende de f1.
        var json = """[{"id":1,"title":"foundation","priority":2},{"id":2,"title":"depends","priority":1,"dependsOn":[1]}]""";
        DevelopmentTasks.Plan(PlanCmd(json, "dotnet test", _targetDir));
        DevelopmentTasks.Bearings(Cmd("bearings", "ok"));

        Assert.Equal("1", StateStore.Get("current_feature_id"));
    }

    [Fact]
    public void Pick_SemFeatureProntaMasComPendencia_EncerraSemReportarConcluido()
    {
        // Blocked graph written directly via Write (bypassing Parse's validation).
        Plan(); // populates RunConfig; the list will be overwritten next
        FeatureStore.Write([
            new Feature(1, "A", 1, false, [2]),
            new Feature(2, "B", 2, false, [1]),
        ]);
        DevelopmentTasks.Bearings(Cmd("bearings", "ok"));
        var result = DevelopmentTasks.Smoke(Cmd("smoke", "ok"));

        Assert.Equal("stop", result);
        Assert.Equal(2, FeatureStore.PendingCount()); // nada foi marcado como passando
    }
}
