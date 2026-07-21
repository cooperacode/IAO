namespace Harness.Engine;

/// <summary>
/// Estado persistido entre invocações: contador de passos + dados acumulados do domínio.
/// Tipo de topo (não aninhado) para ser servível pelo source generator do System.Text.Json,
/// requisito do Native AOT.
/// </summary>
public record HarnessState(int Step, Dictionary<string, string> Data)
{
    // Custo acumulado do run, insumo do teto de custo (ver TaskRegistry). Propriedades
    // init (não posicionais) para não quebrar os `new HarnessState(0, new())` existentes;
    // fora do Data para não poluir a checagem de completude da avaliação.

    /// <summary>Chars de instrução emitidos até aqui — o proxy de custo (soma dos <c>InstructionChars</c>).</summary>
    public int CostChars { get; init; }

    /// <summary>
    /// Contexto do driver (ex.: <c>{"driver":"claude code"}</c>) capturado no envelope
    /// <c>start</c> — sobrevive entre invocações para que <see cref="PromptFormatter"/>
    /// possa reinjetá-lo em toda saída sem que cada task o repasse manualmente.
    /// </summary>
    public Dictionary<string, string>? Context { get; init; }
}
