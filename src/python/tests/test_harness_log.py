"""harness.log is the persisted, human-readable counterpart to what used to be
stderr-only diagnostics, plus the step entry/exit markers that make an in-flight step
observable before it completes (see test_task_registry for the entry-before-action and
fault-logging regressions)."""

from pathlib import Path

from harness_engine import harness_log

FILE_PATH = Path(".harness", "harness.log")


def test_info_grava_uma_linha_com_nivel_e_mensagem():
    harness_log.info("[step 1] enter 'start'")

    line = FILE_PATH.read_text().strip()
    assert "[INFO]" in line
    assert "[step 1] enter 'start'" in line


def test_error_grava_no_arquivo(capsys):
    harness_log.error("[harness] something failed")

    line = FILE_PATH.read_text().strip()
    assert "[ERROR]" in line
    assert "[harness] something failed" in line
    assert "[harness] something failed" in capsys.readouterr().err


def test_reset_apaga_o_arquivo():
    harness_log.info("first run")
    assert FILE_PATH.exists()

    harness_log.reset()

    assert not FILE_PATH.exists()


def test_sem_arquivo_ainda_reset_nao_lanca_excecao():
    harness_log.reset()
