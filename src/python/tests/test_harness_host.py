"""harness_host freezes the evidence (trajectory + state) when a flow completes. The
regression that matters: evaluation — which also ends in `stop` — must NOT overwrite
refinement's evidence, or a re-evaluation reads the wrong trace."""

from pathlib import Path

from harness_engine import harness_host, state_store, trace

FINALIZE_TASK = {"finalize": lambda _e: "stop"}


def test_run_ao_concluir_congela_trajetoria_e_estado_no_caminho_do_flow():
    state_store.set("description", "x")

    harness_host.run(['{"type":"command","value":"finalize"}'], FINALIZE_TASK)

    assert Path(trace.LAST_RUN_PATH).exists()
    assert Path(state_store.LAST_RUN_STATE_PATH).exists()
    assert state_store.load_from(state_store.LAST_RUN_STATE_PATH).data.get("description") == "x"


def test_run_avaliacao_nao_sobrescreve_a_evidencia_do_refinamento():
    # 1) Refinement completes → last-run.* keeps refinement's evidence.
    state_store.set("description", "refinement")
    harness_host.run(['{"type":"command","value":"finalize"}'], FINALIZE_TASK)
    refinement_trace = Path(trace.LAST_RUN_PATH).read_text()

    # 2) Evaluation completes using ITS OWN paths (last-evaluation.*).
    harness_host.run(
        ['{"type":"text","value":"start"}'],
        {"start": lambda _e: "stop"},
        trace.LAST_EVALUATION_PATH,
        state_store.LAST_EVALUATION_STATE_PATH,
    )

    # Evaluation wrote its own evidence...
    assert Path(trace.LAST_EVALUATION_PATH).exists()
    # ...and did NOT touch refinement's.
    assert Path(trace.LAST_RUN_PATH).read_text() == refinement_trace
    assert state_store.load_from(state_store.LAST_RUN_STATE_PATH).data.get("description") == "refinement"
