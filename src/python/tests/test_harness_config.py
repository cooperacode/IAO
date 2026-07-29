"""External config (`harness.json`): missing or invalid NEVER brings down the run — falls
back to defaults; partial only fills in what came in (zero = disabled, only for the
cost ceilings)."""

import os
from pathlib import Path

import pytest

from harness_engine import harness_config

CONFIG_PATH = "harness.json"


@pytest.fixture(autouse=True)
def _clear_timeout_env_var():
    os.environ.pop("HARNESS_TIMEOUT_MS", None)
    yield
    os.environ.pop("HARNESS_TIMEOUT_MS", None)


def test_load_sem_arquivo_usa_defaults():
    config = harness_config.load()

    assert config == harness_config.DEFAULT
    assert config.max_steps == 12
    assert config.max_instruction_chars == 0  # cost ceiling disabled by default
    assert config.timeout_ms == 0  # time guard disabled by default


def test_load_com_timeout_le_e_normaliza():
    Path(CONFIG_PATH).write_text('{"timeoutMs":30000}')

    assert harness_config.load().timeout_ms == 30000

    # A negative value is normalized to 0 (disabled), like the cost ceiling.
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
    Path(CONFIG_PATH).write_text("{ this is not json ")

    config = harness_config.load()

    assert config == harness_config.DEFAULT


def test_load_timeout_acima_do_teto_clampa_no_maximo_permitido():
    # harness.json lives in the supervised agent's working directory: even if it edits
    # the file to grant itself a huge timeout, the hard ceiling prevails.
    Path(CONFIG_PATH).write_text('{"timeoutMs":99999999}')

    assert harness_config.load().timeout_ms == 5 * 60_000


def test_load_com_env_var_sobrepoe_o_timeout_do_arquivo():
    Path(CONFIG_PATH).write_text('{"timeoutMs":1000}')
    os.environ["HARNESS_TIMEOUT_MS"] = "2000"

    assert harness_config.load().timeout_ms == 2000


def test_load_env_var_tambem_respeita_o_teto():
    os.environ["HARNESS_TIMEOUT_MS"] = "99999999"

    assert harness_config.load().timeout_ms == 5 * 60_000


def test_load_env_var_invalida_e_ignorada():
    Path(CONFIG_PATH).write_text('{"timeoutMs":1000}')
    os.environ["HARNESS_TIMEOUT_MS"] = "not a number"

    assert harness_config.load().timeout_ms == 1000
