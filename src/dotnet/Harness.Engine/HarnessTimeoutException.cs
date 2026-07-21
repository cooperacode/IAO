namespace Harness.Engine;

/// <summary>
/// Estourou o timeout de execução de um passo (ver <see cref="HarnessConfig.TimeoutMs"/>).
/// Lançada dentro do <see cref="TaskRegistry"/> e capturada ali mesmo: vira diagnóstico no
/// stderr + <c>"stop"</c> no stdout — o mesmo contrato de encerramento gracioso das demais
/// guardas (teto de passos e de custo).
/// </summary>
public sealed class HarnessTimeoutException(int timeoutMs)
    : Exception($"timeout de {timeoutMs}ms excedido na execução da task; encerrando.");
