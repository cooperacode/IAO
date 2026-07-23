from pathlib import Path

from harness_engine import git_command


def test_run_comando_valido_captura_stdout():
    result = git_command.run(Path.cwd(), "--version")

    assert result.exit_code == 0
    assert "git version" in result.output


def test_run_diretorio_inexistente_retorna_erro_sem_lancar(tmp_path):
    missing = tmp_path / "missing"

    result = git_command.run(missing, "status")

    assert result.exit_code == -1
    assert result.error
