namespace Harness.Engine;

/// <summary>
/// Avaliação em lote sobre um golden set — é o <c>Task Registry (#2)</c> virando registry
/// de avaliação de verdade: em vez de datasets MMLU/HumanEval, casos de refinamento com a
/// trajetória e as chaves esperadas. Puramente determinístico (0 tokens): compara a
/// evidência gravada de cada run contra a expectativa do caso e agrega a taxa de acerto.
/// </summary>
public static class BatchEvaluator
{
    public static CaseResult Evaluate(GoldenCase golden, IReadOnlyList<TraceEntry> trace, HarnessState finalState) =>
        new(golden.Id,
        [
            Evaluators.Trajectory(golden.ExpectedTrajectory, Evaluators.CommandsOf(trace)),
            Evaluators.StepBudget(trace),
            Evaluators.Completeness(finalState, golden.RequiredKeys),
        ],
        golden.ExpectPass);

    public static BatchResult EvaluateAll(
        IEnumerable<(GoldenCase Golden, IReadOnlyList<TraceEntry> Trace, HarnessState State)> runs) =>
        new(runs.Select(r => Evaluate(r.Golden, r.Trace, r.State)).ToList());
}

/// <summary>
/// Um caso do golden set: o esperado contra o qual a evidência gravada é medida.
/// <see cref="ExpectPass"/> = <c>false</c> marca um caso <b>negativo intencional</b> — um run
/// que DEVE reprovar nas métricas (ex.: trajetória perfeita mas conteúdo faltante), usado
/// para provar que os evaluators pegam a falha. O padrão é <c>true</c>.
/// </summary>
public record GoldenCase(
    string Id, string Description, string[] ExpectedTrajectory, string[] RequiredKeys, bool ExpectPass = true);

/// <summary>
/// Notas determinísticas de um caso. <see cref="Passed"/> exige acerto pleno nas métricas;
/// <see cref="Ok"/> é o veredito da suíte — o caso se comportou como o golden set esperava
/// (um caso negativo intencional é <see cref="Ok"/> justamente quando <see cref="Passed"/> é falso).
/// </summary>
public record CaseResult(string Id, IReadOnlyList<Score> Scores, bool ExpectedPass = true)
{
    public bool Passed => Scores.All(s => s.Passed);
    public bool Ok => Passed == ExpectedPass;
}

/// <summary>Agregado do lote: fração de casos que se comportaram como esperado (pronto para CI).</summary>
public record BatchResult(IReadOnlyList<CaseResult> Cases)
{
    public int Total => Cases.Count;
    public int PassedCount => Cases.Count(c => c.Ok);
    public double PassRate => Total == 0 ? 0.0 : (double)PassedCount / Total;
}
