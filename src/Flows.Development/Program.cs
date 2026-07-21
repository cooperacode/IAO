using Harness.Engine;
using Flows.Development;

// Padrão "long-running agent": inicializador + loop de sessões frescas, uma feature por vez.
// Nenhuma orquestração aqui — dispatch, guardas e transporte vivem em Harness.Engine.
// start → plan → [bearings → smoke → pick → implement → verify → handoff]*
var tasks = new Dictionary<string, Func<Envelope?, string>>
{
    ["start"] = _ => DevelopmentTasks.Start(),
    ["plan"] = envelope => DevelopmentTasks.Plan(envelope),
    ["bearings"] = envelope => DevelopmentTasks.Bearings(envelope),
    ["smoke"] = envelope => DevelopmentTasks.Smoke(envelope),
    ["pick"] = envelope => DevelopmentTasks.Pick(envelope),
    ["implement"] = envelope => DevelopmentTasks.Implement(envelope),
    ["verify"] = envelope => DevelopmentTasks.Verify(envelope),
    ["handoff"] = envelope => DevelopmentTasks.Handoff(envelope),
};

// Expectativa contextual por comando; recusa vira erro corretivo (o driver corrige e reenvia).
// `pick` não tem validador — não carrega artefato do driver (a seleção é do harness).
var validators = new Dictionary<string, Func<Envelope, ValidationResult>>
{
    ["plan"] = EnvelopeValidation.NotEmpty("o array JSON de features [{id,title,priority}]"),
    ["bearings"] = EnvelopeValidation.NotEmpty("o resumo da orientação (pwd, progress, git log)"),
    ["smoke"] = EnvelopeValidation.NotEmpty("o resultado do smoke test (init.sh)"),
    ["implement"] = EnvelopeValidation.NotEmpty("o resumo do que foi implementado"),
    ["verify"] = EnvelopeValidation.Matches(
        @"^(PASS\b|FAIL\b)",
        "o veredito do self-verify começando com PASS ou FAIL: motivo"),
    ["handoff"] = EnvelopeValidation.Matches(
        @"^([0-9a-f]{6,40}\b|NO_GIT:\s+\S.*)$",
        "o hash do commit ou NO_GIT: motivo quando nao houver repositorio Git"),
};

// Snapshots próprios: se este flow dividir o `.harness/` com o refinamento+avaliação (mesmo
// workspace), ele NÃO pode sobrescrever o last-run.* que a avaliação consome. Congela no seu
// próprio caminho — como a avaliação faz com last-evaluation.*.
// maxSteps: override do teto global (12) — este flow é long-running e precisa de folga p/ o loop.
return HarnessHost.Run(
    args, tasks,
    traceSnapshotPath: ".harness/last-development.trace.jsonl",
    stateSnapshotPath: ".harness/last-development.state.json",
    validators: validators,
    maxSteps: DevelopmentTasks.StepBudget);
