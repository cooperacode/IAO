//! Erros tipados do harness.

/// Estourou o timeout de execução de um passo (ver `harness_config::timeout_ms`). Lançado
/// e capturado dentro de `task_registry`: vira diagnóstico no stderr + `"stop"` no stdout
/// — o mesmo contrato de encerramento gracioso das demais guardas (teto de passos e de
/// custo).
#[derive(Debug, thiserror::Error)]
#[error("timeout de {timeout_ms}ms excedido na execução da task; encerrando.")]
pub struct HarnessTimeoutError {
    pub timeout_ms: i32,
}
