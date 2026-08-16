"""Regression coverage for a real production crash in the .NET port that this Python port
mirrors (evaluator.py's docstring: "This mirrors SpecificationEvaluator.cs"). A DataRule with
an empty/null `rule` used to sail through SRS evaluation, get accepted, and only break much
later — as a NullReferenceException inside the .NET renderer's Escape() — when `approve` tried
to publish it. The evaluator is the gate that's supposed to reject a structurally-invalid
document before it is ever accepted, so an empty required text field belongs here."""

from flows_specification import evaluator, renderer


def _valid_srs(prd_digest="sha256:prd"):
    return {
        "schema": "iao/srs/v1",
        "prdDigest": prd_digest,
        "functionalRequirements": [
            {
                "id": "RF-001",
                "goalIds": ["OBJ-001"],
                "statement": "does the thing",
                "dependsOn": [],
                "acceptanceIds": ["AC-001"],
            }
        ],
        "qualityRequirements": [],
        "acceptanceCriteria": [
            {"id": "AC-001", "requirementIds": ["RF-001"], "given": "given", "when": "when", "then": "then"}
        ],
        "interfaces": [],
        "dataRules": [],
        "delivery": {"target": "target", "verificationStrategy": "strategy", "isBootstrap": True},
    }


def test_evaluate_srs_srs_valido_passa():
    result = evaluator.srs(_valid_srs(), "sha256:prd", ["OBJ-001"])

    assert result["passed"]
    assert result["violations"] == []


def test_evaluate_srs_regra_de_dados_sem_texto_e_rejeitada():
    document = _valid_srs()
    document["dataRules"] = [{"id": "DR-001", "requirementIds": ["RF-001"], "rule": None}]

    result = evaluator.srs(document, "sha256:prd", ["OBJ-001"])

    assert not result["passed"]
    codes = [v["code"] for v in result["violations"]]
    assert "SRS_DATA_RULE_TEXT_MISSING" in codes


def test_evaluate_srs_regra_de_dados_com_chave_ausente_e_rejeitada():
    # Not just `null` — a key missing entirely from the accepted JSON must be caught the same
    # way (dict.get returns None either way).
    document = _valid_srs()
    document["dataRules"] = [{"id": "DR-001", "requirementIds": ["RF-001"]}]

    result = evaluator.srs(document, "sha256:prd", ["OBJ-001"])

    assert not result["passed"]
    assert any(v["code"] == "SRS_DATA_RULE_TEXT_MISSING" for v in result["violations"])


def test_evaluate_srs_campos_de_texto_vazios_em_outros_registros_sao_rejeitados():
    document = _valid_srs()
    document["functionalRequirements"][0]["statement"] = "   "
    document["acceptanceCriteria"][0]["given"] = ""
    document["interfaces"] = [{"id": "IF-001", "requirementIds": ["RF-001"], "name": "name", "description": None}]
    document["delivery"] = {"target": "", "verificationStrategy": "strategy", "isBootstrap": True}

    result = evaluator.srs(document, "sha256:prd", ["OBJ-001"])

    assert not result["passed"]
    codes = [v["code"] for v in result["violations"]]
    assert "SRS_REQUIREMENT_STATEMENT_MISSING" in codes
    assert "SRS_ACCEPTANCE_CRITERION_TEXT_MISSING" in codes
    assert "SRS_INTERFACE_TEXT_MISSING" in codes
    assert "SRS_DELIVERY_TEXT_MISSING" in codes


def test_render_srs_campo_de_texto_nulo_nao_lanca_excecao():
    # Renderer-level regression test: even if a bad document somehow reaches render (an
    # already-accepted document from before this fix, for instance), it must not crash the
    # whole harness turn over one empty field.
    document = _valid_srs()
    document["dataRules"] = [{"id": "DR-001", "requirementIds": ["RF-001"], "rule": None}]

    rendered = renderer.srs(document)

    assert "**DR-001**" in rendered


def _valid_sdd(srs_digest="sha256:srs"):
    return {
        "schema": "iao/sdd/v1",
        "srsDigest": srs_digest,
        "adrs": [
            {
                "id": "ADR-1",
                "title": "title",
                "decision": "decision",
                "rationale": "rationale",
                "requirementIds": ["RF-001"],
            }
        ],
        "controls": [
            {
                "id": "IC-1",
                "name": "control",
                "description": "description",
                "requirementIds": ["RF-001"],
            }
        ],
    }


def test_evaluate_sdd_design_source_backed_passa():
    document = _valid_sdd()
    document.update(
        {
            "sourceDigest": "sha256:sources",
            "sourceFiles": ["architecture.md", "tree.txt"],
            "designContent": "## Architecture\n\n```mermaid\ngraph TD\n```\n\n```text\napp/\n```",
        }
    )

    result = evaluator.sdd(
        document,
        "sha256:srs",
        ["RF-001"],
        "sha256:sources",
        document["sourceFiles"],
    )

    assert result["passed"]


def test_evaluate_sdd_design_source_backed_sem_conteudo_e_rejeitado():
    document = _valid_sdd()
    document.update(
        {"sourceDigest": "sha256:sources", "sourceFiles": ["architecture.md"]}
    )

    result = evaluator.sdd(
        document,
        "sha256:srs",
        ["RF-001"],
        "sha256:sources",
        document["sourceFiles"],
    )

    assert not result["passed"]
    assert any(v["code"] == "SDD_DESIGN_CONTENT_MISSING" for v in result["violations"])


def test_render_sdd_preserva_markdown_de_design():
    document = _valid_sdd()
    document["designContent"] = "## Architecture\n\n```mermaid\ngraph TD\n```\n\n```text\napp/\n```"

    rendered = renderer.sdd(document)

    assert "```mermaid\ngraph TD\n```" in rendered
    assert "app/" in rendered
