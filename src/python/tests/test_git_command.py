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


def test_run_injeta_isolamento_de_hooks_e_pager(tmp_path):
    # `git config --get` sees `-c` overrides on the config stack, so we can confirm
    # run() always injects them without needing a real repository.
    hooks_path = git_command.run(tmp_path, "config", "--get", "core.hooksPath")
    assert hooks_path.exit_code == 0
    assert hooks_path.output.strip().endswith("iao-no-hooks")

    pager = git_command.run(tmp_path, "config", "--get", "core.pager")
    assert pager.exit_code == 0
    assert pager.output.strip() == "cat"
