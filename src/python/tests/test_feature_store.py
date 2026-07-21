"""feature_list.json é o "persistent artifact" que atravessa os hard resets de contexto
do flow de desenvolvimento: seleção determinística da próxima pendente e término quando
todas passam. Mesma tolerância dos demais stores — ausente/ilegível → lista vazia, nunca
derruba."""

from pathlib import Path

from harness_engine import feature_store
from harness_engine.feature_store import Feature


def test_write_e_load_fazem_roundtrip():
    feature_store.write([Feature(1, "A", 2, False), Feature(2, "B", 1, False)])

    loaded = feature_store.load()

    assert len(loaded) == 2
    assert loaded[0].title == "A"


def test_parse_array_cru_forca_pendente_e_preserva_campos():
    features = feature_store.parse(
        '[{"id":1,"title":"Login","priority":1},{"id":2,"title":"Logout","priority":3}]'
    )

    assert len(features) == 2
    assert all(not f.passes for f in features)  # toda feature nasce pendente
    assert features[0].title == "Login"


def test_parse_sem_id_reindexa():
    features = feature_store.parse('[{"title":"X","priority":1},{"title":"Y","priority":1}]')

    assert [f.id for f in features] == [1, 2]


def test_parse_json_invalido_retorna_vazio_sem_lancar():
    assert feature_store.parse("isso não é json") == []
    assert feature_store.parse("[]") == []


def test_next_pending_escolhe_maior_prioridade_pendente():
    feature_store.write([
        Feature(1, "baixa", 3, False),
        Feature(2, "alta", 1, False),
        Feature(3, "media", 2, True),  # já passa — ignorada
    ])

    assert feature_store.next_pending().id == 2  # prioridade 1


def test_parse_depends_on_ausente_normaliza_para_array_vazio():
    features = feature_store.parse('[{"id":1,"title":"X","priority":1}]')

    assert features[0].deps == ()


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
    # Simula um feature_list.json gravado por uma versão anterior do harness, sem a chave
    # "dependsOn" — prova a compatibilidade retroativa que motivou o design com `deps`.
    Path(".harness").mkdir(exist_ok=True)
    Path(".harness/feature_list.json").write_text(
        '{"items":[{"id":1,"title":"A","priority":1,"passes":false}]}'
    )

    loaded = feature_store.load()

    assert len(loaded) == 1
    assert loaded[0].deps == ()


def test_next_pending_ignora_feature_com_dependencia_pendente():
    feature_store.write([
        Feature(1, "fundação", 2, False),
        Feature(2, "depende de 1", 1, False, (1,)),  # prioridade "melhor", mas bloqueada
    ])

    assert feature_store.next_pending().id == 1


def test_next_pending_libera_feature_apos_dependencia_passar():
    feature_store.write([
        Feature(1, "fundação", 2, False),
        Feature(2, "depende de 1", 1, False, (1,)),
    ])
    assert feature_store.next_pending().id == 1

    feature_store.mark_passed(1)

    assert feature_store.next_pending().id == 2


def test_next_pending_todas_bloqueadas_retorna_none_com_pendencias_existentes():
    # Grafo cíclico gravado direto via write (bypassando a validação de parse) — simula um
    # feature_list.json editado à mão fora do fluxo normal.
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
    assert not feature_store.all_passing()  # nada gravado → não é "tudo passando"


def test_reset_apaga_a_lista():
    feature_store.write([Feature(1, "A", 1, False)])
    feature_store.reset()

    assert feature_store.load() == []
