"""feature_list.json is the "persistent artifact" that survives the development flow's
context hard resets: deterministic selection of the next pending feature and
termination when all pass. Same tolerance as the other stores — missing/unreadable →
empty list, never brings down the run."""

from pathlib import Path

from harness_engine import feature_store
from harness_engine.feature_store import Feature


def test_write_e_load_fazem_roundtrip():
    feature_store.write([Feature(1, "A", 2, False), Feature(2, "B", 1, False)])

    loaded = feature_store.load()

    assert len(loaded) == 2
    assert loaded[0].title == "A"


def test_write_formata_json_para_leitura():
    feature_store.write([Feature(1, "A", 2, False)])

    json = Path(".harness/feature_list.json").read_text()

    assert "\n" in json
    assert '  "items": [' in json
    assert '      "title": "A"' in json


def test_parse_array_cru_forca_pendente_e_preserva_campos():
    features = feature_store.parse(
        '[{"id":1,"title":"Login","priority":1},{"id":2,"title":"Logout","priority":3}]'
    )

    assert len(features) == 2
    assert all(not f.passes for f in features)  # every feature is born pending
    assert features[0].title == "Login"


def test_parse_sem_id_reindexa():
    features = feature_store.parse('[{"title":"X","priority":1},{"title":"Y","priority":1}]')

    assert [f.id for f in features] == [1, 2]


def test_parse_json_invalido_retorna_vazio_sem_lancar():
    assert feature_store.parse("this is not json") == []
    assert feature_store.parse("[]") == []


def test_next_pending_escolhe_maior_prioridade_pendente():
    feature_store.write([
        Feature(1, "low", 3, False),
        Feature(2, "high", 1, False),
        Feature(3, "medium", 2, True),  # already passing — ignored
    ])

    assert feature_store.next_pending().id == 2  # priority 1


def test_parse_depends_on_ausente_normaliza_para_array_vazio():
    features = feature_store.parse('[{"id":1,"title":"X","priority":1}]')

    assert features[0].deps == ()


def test_parse_description_e_references_ausentes_normalizam_para_vazio():
    features = feature_store.parse('[{"id":1,"title":"X","priority":1}]')

    assert features[0].description == ""
    assert features[0].refs == ()


def test_parse_preserva_description_e_references():
    features = feature_store.parse(
        '[{"id":1,"title":"X","priority":1,"description":"does Y","references":["RF-003"]}]'
    )

    assert features[0].description == "does Y"
    assert features[0].refs == ("RF-003",)


def test_parse_description_acima_do_teto_e_truncada():
    long_description = "a" * (feature_store.DESCRIPTION_MAX_CHARS + 50)

    features = feature_store.parse(
        f'[{{"id":1,"title":"X","priority":1,"description":"{long_description}"}}]'
    )

    assert len(features[0].description) == feature_store.DESCRIPTION_MAX_CHARS


def test_parse_depends_on_ciclico_retorna_vazio_sem_lancar():
    features = feature_store.parse(
        '[{"id":1,"title":"A","priority":1,"dependsOn":[2]},'
        '{"id":2,"title":"B","priority":2,"dependsOn":[1]}]'
    )

    assert features == []


def test_parse_depends_on_auto_referencia_retorna_vazio():
    features = feature_store.parse('[{"id":1,"title":"A","priority":1,"dependsOn":[1]}]')

    assert features == []


def test_parse_depends_on_id_inexistente_retorna_vazio():
    features = feature_store.parse('[{"id":1,"title":"A","priority":1,"dependsOn":[99]}]')

    assert features == []


def test_load_feature_list_legado_sem_depends_on_nao_lanca():
    # Simulates a feature_list.json written by an earlier harness version, without the
    # "dependsOn" key — proves the backward compatibility that motivated the `deps` design.
    Path(".harness").mkdir(exist_ok=True)
    Path(".harness/feature_list.json").write_text(
        '{"items":[{"id":1,"title":"A","priority":1,"passes":false}]}'
    )

    loaded = feature_store.load()

    assert len(loaded) == 1
    assert loaded[0].deps == ()


def test_next_pending_ignora_feature_com_dependencia_pendente():
    feature_store.write([
        Feature(1, "foundation", 2, False),
        Feature(2, "depends on 1", 1, False, (1,)),  # "better" priority, but blocked
    ])

    assert feature_store.next_pending().id == 1


def test_next_pending_libera_feature_apos_dependencia_passar():
    feature_store.write([
        Feature(1, "foundation", 2, False),
        Feature(2, "depends on 1", 1, False, (1,)),
    ])
    assert feature_store.next_pending().id == 1

    feature_store.mark_passed(1)

    assert feature_store.next_pending().id == 2


def test_next_pending_todas_bloqueadas_retorna_none_com_pendencias_existentes():
    # Cyclic graph written directly via write (bypassing parse's validation) — simulates
    # a feature_list.json hand-edited outside the normal flow.
    feature_store.write([
        Feature(1, "A", 1, False, (2,)),
        Feature(2, "B", 2, False, (1,)),
    ])

    assert feature_store.next_pending() is None
    assert feature_store.pending_count() == 2


def test_mark_passed_vira_a_feature_e_all_passing_fecha_quando_todas_passam():
    feature_store.write([Feature(1, "A", 1, False), Feature(2, "B", 2, False)])

    feature_store.mark_passed(1)
    assert feature_store.pending_count() == 1
    assert not feature_store.all_passing()

    feature_store.mark_passed(2)
    assert feature_store.pending_count() == 0
    assert feature_store.all_passing()
    assert feature_store.next_pending() is None


def test_all_passing_lista_vazia_e_falso():
    assert not feature_store.all_passing()  # nothing written → not "all passing"


def test_reset_apaga_a_lista():
    feature_store.write([Feature(1, "A", 1, False)])
    feature_store.reset()

    assert feature_store.load() == []
