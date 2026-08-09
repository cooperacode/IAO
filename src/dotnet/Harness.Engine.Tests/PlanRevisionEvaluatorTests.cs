using Harness.Engine;

namespace Harness.Engine.Tests;

public class PlanRevisionEvaluatorTests : IDisposable
{
    private readonly PlanObservation _observation;

    public PlanRevisionEvaluatorTests()
    {
        FeatureStore.Reset();
        _observation = PlanObservationStore.Append(
            "verification_failure", 2, "API verification failed repeatedly.", "exit 1");
    }

    public void Dispose() => FeatureStore.Reset();

    private static Feature FeatureWithCoverage(int id, string title, int priority, bool passes = false) =>
        new(id, title, priority, passes, References: ["RF-001"],
            ImplementationContext: new ImplementationContext(Acceptance: ["returns HTTP 401"]));

    private PlanRevision Revision(params Feature[] features) => new(
        "new evidence requires a global dependency",
        ["keep the stub", "introduce the dependency; selected because it preserves behavior"],
        features,
        [_observation.Id]);

    [Fact]
    public void Evaluate_AprovaMudancaRastreavelQuePreservaCobertura()
    {
        var current = new[] { FeatureWithCoverage(1, "API", 2) };
        var revision = Revision(
            FeatureWithCoverage(1, "API", 2) with { DependsOn = [2] },
            new Feature(2, "Authentication", 1, false));

        var result = PlanRevisionEvaluator.Evaluate(
            current, revision, PlanObservationStore.Load(), 10, 80, 8);

        Assert.Equal(PlanRevisionVerdict.Approve, result.Verdict);
        Assert.Empty(result.Errors);
        Assert.Equal([2], result.Diff.Added);
        Assert.Equal([1], result.Diff.Modified);
    }

    [Fact]
    public void Evaluate_RejeitaPerdaDeReferenciaEAcceptance()
    {
        var current = new[] { FeatureWithCoverage(1, "API", 2) };
        var revision = Revision(new Feature(1, "API reduced", 2, false));

        var result = PlanRevisionEvaluator.Evaluate(
            current, revision, PlanObservationStore.Load(), 10, 80, 8);

        Assert.Equal(PlanRevisionVerdict.Reject, result.Verdict);
        Assert.Contains(result.Errors, e => e.Code == "REQUIREMENT_COVERAGE_REMOVED");
        Assert.Contains(result.Errors, e => e.Code == "ACCEPTANCE_REMOVED");
    }

    [Fact]
    public void Evaluate_RejeitaObservacaoDesconhecidaEPlanoSemMudanca()
    {
        var current = new[] { new Feature(1, "API", 1, false) };
        var revision = new PlanRevision("retry", ["A", "B"], [.. current], ["OBS-999"]);

        var result = PlanRevisionEvaluator.Evaluate(
            current, revision, PlanObservationStore.Load(), 10, 80, 8);

        Assert.Contains(result.Errors, e => e.Code == "OBSERVATION_UNKNOWN");
        Assert.Contains(result.Errors, e => e.Code == "PLAN_UNCHANGED");
    }

    [Fact]
    public void Evaluate_AprovaComAvisoQuandoOrcamentoPodeSerInsuficiente()
    {
        var current = new[] { new Feature(1, "API", 1, false) };
        var revision = Revision(
            new Feature(1, "API", 2, false),
            new Feature(2, "Auth", 1, false));

        var result = PlanRevisionEvaluator.Evaluate(
            current, revision, PlanObservationStore.Load(), 10, remainingSteps: 4, stepsPerFeature: 8);

        Assert.Equal(PlanRevisionVerdict.ApproveWithWarnings, result.Verdict);
        Assert.Contains(result.Warnings, warning => warning.Code == "BUDGET_RISK");
    }

    [Fact]
    public void Evaluate_RejeitaPlanoJaAplicadoNoHistorico()
    {
        var current = new[] { new Feature(1, "API", 2, false) };
        var revision = Revision(
            new Feature(1, "API", 2, false, [2]),
            new Feature(2, "Auth", 1, false));
        var first = PlanRevisionEvaluator.Evaluate(
            current, revision, PlanObservationStore.Load(), 10, 80, 8);
        PlanRevisionStore.Record(revision, revision.Features, first);

        var repeated = PlanRevisionEvaluator.Evaluate(
            current, revision, PlanObservationStore.Load(), 10, 80, 8);

        Assert.Contains(repeated.Errors, error => error.Code == "PLAN_REPEATED");
    }
}
