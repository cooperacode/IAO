namespace Harness.Engine;

/// <summary>
/// Ponto de entrada reutilizável de um flow. Um novo domínio só precisa definir suas
/// tasks e chamar <see cref="Run"/> — toda a orquestração (dispatch, guardas, transporte)
/// fica aqui.
/// </summary>
public static class HarnessHost
{
    public static int Run(
        string[] args,
        IReadOnlyDictionary<string, Func<Envelope?, string>> tasks,
        string traceSnapshotPath = Trace.LastRunPath,
        string stateSnapshotPath = StateStore.LastRunStatePath,
        IReadOnlyDictionary<string, Func<Envelope, ValidationResult>>? validators = null,
        int? maxSteps = null)
    {
        var result = TaskRegistry.Dispatch(args, tasks, validators, maxSteps);

        // Run concluído: congela trajetória E estado final como evidência para a avaliação
        // posterior, antes que um próximo flow resete o trace e o state vivos. Cada flow
        // publica no SEU caminho (o refinamento em last-run.*, a avaliação em
        // last-evaluation.*), para que a avaliação não sobrescreva o que ela mesma consome.
        if (result == "stop")
        {
            Trace.Snapshot(traceSnapshotPath);
            StateStore.Snapshot(stateSnapshotPath);
        }

        // Único ponto que escreve no stdout — o canal de transporte do harness.
        Console.WriteLine(result);
        return 0;
    }
}
