"""Config externa (`harness.json`): ausente ou inválida NUNCA derruba o run — cai nos
defaults; parcial preenche só o que veio (zero = desligado apenas nos tetos de custo)."""

from pathlib import Path

from harness_engine import harness_config

CONFIG_PATH = "harness.json"


def test_load_sem_arquivo_usa_defaults():
    config = harness_config.load()

    assert config == harness_config.DEFAULT
    assert config.max_steps == 12
    assert config.max_instruction_chars == 0  # teto de custo desligado por padrão
    assert config.timeout_ms == 0  # guarda de tempo desligada por padrão


def test_load_com_timeout_le_e_normaliza():
    Path(CONFIG_PATH).write_text('{"timeoutMs":30000}')

    assert harness_config.load().timeout_ms == 30000

    # Valor negativo é normalizado para 0 (desligado), como o teto de custo.
    Path(CONFIG_PATH).write_text('{"timeoutMs":-5}')
    assert harness_config.load().timeout_ms == 0


def test_load_com_arquivo_usa_os_valores_do_arquivo():
    Path(CONFIG_PATH).write_text(
        '{"maxSteps":5,"maxInstructionChars":20000,"docsMaxChars":10000,"docsFolder":"specs"}'
    )

    config = harness_config.load()

    assert config.max_steps == 5
    assert config.max_instruction_chars == 20000
    assert config.docs_max_chars == 10000
    assert config.docs_folder == "specs"


def test_load_arquivo_parcial_completa_com_defaults():
    Path(CONFIG_PATH).write_text('{"maxInstructionChars":8000}')

    config = harness_config.load()

    assert config.max_instruction_chars == 8000
    assert config.max_steps == harness_config.DEFAULT.max_steps
    assert config.docs_max_chars == harness_config.DEFAULT.docs_max_chars
    assert config.docs_folder == harness_config.DEFAULT.docs_folder


def test_load_arquivo_invalido_cai_nos_defaults_sem_lancar():
    Path(CONFIG_PATH).write_text("{ isso não é json ")

    config = harness_config.load()

    assert config == harness_config.DEFAULT
