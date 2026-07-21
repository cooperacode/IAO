"""harness_host congela a evidência (trajetória + estado) ao concluir um flow. A
regressão que importa: a avaliação — que também termina em `stop` — NÃO pode sobrescrever
a evidência do refinamento, senão a reavaliação lê o trace errado."""

from pathlib import Path

from harness_engine import harness_host, state_store, trace

FINALIZE_TASK = {"finalize": lambda _e: "stop"}


def test_run_ao_concluir_congela_trajetoria_e_estado_no_caminho_do_flow():
    state_store.set("descricao", "x")

    harness_host.run(['{"type":"command","value":"finalize"}'], FINALIZE_TASK)

    assert Path(trace.LAST_RUN_PATH).exists()
    assert Path(state_store.LAST_RUN_STATE_PATH).exists()
    assert state_store.load_from(state_store.LAST_RUN_STATE_PATH).data.get("descricao") == "x"


def test_run_avaliacao_nao_sobrescreve_a_evidencia_do_refinamento():
    # 1) Refinamento conclui → last-run.* guarda a evidência do refinamento.
    state_store.set("descricao", "refino")
    harness_host.run(['{"type":"command","value":"finalize"}'], FINALIZE_TASK)
    refino_trace = Path(trace.LAST_RUN_PATH).read_text()

    # 2) Avaliação conclui usando os SEUS caminhos (last-evaluation.*).
    harness_host.run(
        ['{"type":"text","value":"start"}'],
        {"start": lambda _e: "stop"},
        trace.LAST_EVALUATION_PATH,
        state_store.LAST_EVALUATION_STATE_PATH,
    )

    # A avaliação gravou a própria evidência...
    assert Path(trace.LAST_EVALUATION_PATH).exists()
    # ...e NÃO tocou na do refinamento.
    assert Path(trace.LAST_RUN_PATH).read_text() == refino_trace
    assert state_store.load_from(state_store.LAST_RUN_STATE_PATH).data.get("descricao") == "refino"
