//! A lista de features do flow de desenvolvimento, persistida em
//! `.harness/feature_list.json` — o "persistent artifact" que atravessa os hard resets de
//! contexto: cada sessão (uma feature) lê e escreve aqui, sem depender do histórico da
//! conversa. Todas nascem com `passes = false`; o flow vira uma por vez até não sobrar
//! nenhuma pendente.
//!
//! Mesma tolerância dos demais stores: ausente ou ilegível → lista vazia, nunca derruba o
//! run.

use std::collections::{HashMap, HashSet, VecDeque};

use serde::{Deserialize, Serialize};

const DIR: &str = ".harness";
const FILE_PATH: &str = ".harness/feature_list.json";

/// Uma feature do backlog de desenvolvimento: prioridade (menor = mais alta), se já passa
/// e de quais outras (por id) depende.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Feature {
    pub id: i32,
    pub title: String,
    pub priority: i32,
    #[serde(default)]
    pub passes: bool,
    #[serde(rename = "dependsOn", default)]
    pub depends_on: Vec<i32>,
}

#[derive(Debug, Serialize, Deserialize)]
struct FeatureList {
    items: Vec<Feature>,
}

// Forma crua do array que o driver devolve no `plan` — `id` é opcional (reindexado pela
// ordem quando ausente/<=0), e `passes` não é lido daqui: toda feature nasce pendente.
#[derive(Debug, Deserialize)]
struct RawFeature {
    #[serde(default)]
    id: i32,
    title: String,
    priority: i32,
    #[serde(rename = "dependsOn", default)]
    depends_on: Vec<i32>,
}

/// Sobrescreve a lista inteira — usada pelo `plan` (session 0) e por `mark_passed`.
pub fn write(features: &[Feature]) {
    if let Err(e) = std::fs::create_dir_all(DIR) {
        eprintln!("[FeatureStore] falha ao gravar: {e}");
        return;
    }
    let list = FeatureList {
        items: features.to_vec(),
    };
    match serde_json::to_string_pretty(&list) {
        Ok(json) => {
            if let Err(e) = crate::atomic_io::write_atomic(std::path::Path::new(FILE_PATH), &json) {
                eprintln!("[FeatureStore] falha ao gravar: {e}");
            }
        }
        Err(e) => eprintln!("[FeatureStore] falha ao gravar: {e}"),
    }
}

/// Interpreta o array cru de features que o driver devolve no `plan`
/// (`[{"id":1,"title":"...","priority":1}, ...]`). Força `passes = false` (toda feature
/// nasce pendente) e reindexa ids ausentes/duplicados pela ordem. Lista vazia se o JSON
/// não interpretar — o caller re-emite o pedido (loop corretivo), não derruba o run.
pub fn parse(json: &str) -> Vec<Feature> {
    let parsed: Vec<RawFeature> = match serde_json::from_str(json) {
        Ok(p) => p,
        Err(e) => {
            eprintln!("[FeatureStore] falha ao interpretar features: {e}");
            return Vec::new();
        }
    };

    if parsed.is_empty() {
        return Vec::new();
    }

    // Reindex primeiro: dependsOn só faz sentido referenciando ids já finais, não os
    // brutos (possivelmente ausentes/duplicados) que vieram do driver.
    let reindexed: Vec<Feature> = parsed
        .into_iter()
        .enumerate()
        .map(|(i, f)| Feature {
            id: if f.id > 0 { f.id } else { (i + 1) as i32 },
            title: f.title,
            priority: f.priority,
            passes: false,
            depends_on: f.depends_on,
        })
        .collect();

    if let Some(error) = dependency_graph_error(&reindexed) {
        eprintln!("[FeatureStore] grafo de dependências inválido: {error}");
        return Vec::new();
    }

    reindexed
}

// `None` se o grafo de `depends_on` é válido (todo id existe, sem ciclo); senão, uma
// descrição do problema. Kahn (ordenação topológica): sobra nó fora do conjunto
// resolvido ⇒ ciclo. Checa dangling ref primeiro — senão uma dependência fantasma seria
// contada como eternamente não-resolvida e reportada como "ciclo" quando na verdade é id
// inválido.
fn dependency_graph_error(features: &[Feature]) -> Option<String> {
    let valid_ids: HashSet<i32> = features.iter().map(|f| f.id).collect();

    let dangling: Vec<String> = features
        .iter()
        .flat_map(|f| {
            f.depends_on
                .iter()
                .filter(|dep| !valid_ids.contains(dep))
                .map(move |dep| format!("{}->{}", f.id, dep))
        })
        .collect();
    if !dangling.is_empty() {
        return Some(format!(
            "dependsOn referencia id(s) inexistente(s): {}",
            dangling.join(", ")
        ));
    }

    // i32 (não usize) de propósito: ids duplicados não são deduplicados hoje pelo reindex
    // acima, então o indegree pode ser decrementado mais vezes que o esperado — não deve
    // panicar por underflow nesse caso de borda, só não fechar o ciclo.
    let mut indegree: HashMap<i32, i32> = HashMap::new();
    for f in features {
        indegree.entry(f.id).or_insert(f.depends_on.len() as i32);
    }

    let mut dependents: HashMap<i32, Vec<i32>> = HashMap::new();
    for f in features {
        for dep in &f.depends_on {
            dependents.entry(*dep).or_default().push(f.id);
        }
    }

    let mut queue: VecDeque<i32> = indegree
        .iter()
        .filter(|&(_, &d)| d == 0)
        .map(|(&id, _)| id)
        .collect();
    let mut resolved: HashSet<i32> = HashSet::new();
    while let Some(id) = queue.pop_front() {
        if !resolved.insert(id) {
            continue;
        }
        if let Some(deps) = dependents.get(&id) {
            for &dependent in deps {
                if let Some(d) = indegree.get_mut(&dependent) {
                    *d -= 1;
                    if *d == 0 {
                        queue.push_back(dependent);
                    }
                }
            }
        }
    }

    if resolved.len() == indegree.len() {
        None
    } else {
        let mut cyclic: Vec<i32> = indegree
            .keys()
            .filter(|id| !resolved.contains(id))
            .copied()
            .collect();
        cyclic.sort_unstable();
        let cyclic = cyclic
            .iter()
            .map(|i| i.to_string())
            .collect::<Vec<_>>()
            .join(", ");
        Some(format!("dependência cíclica entre as features: {cyclic}"))
    }
}

pub fn load() -> Vec<Feature> {
    let p = std::path::Path::new(FILE_PATH);
    if !p.exists() {
        return Vec::new();
    }

    let loaded = std::fs::read_to_string(p)
        .map_err(|e| e.to_string())
        .and_then(|json| serde_json::from_str::<FeatureList>(&json).map_err(|e| e.to_string()));

    match loaded {
        Ok(list) => list.items,
        Err(e) => {
            eprintln!("[FeatureStore] falha ao carregar: {e}");
            Vec::new()
        }
    }
}

/// A próxima feature a implementar: a de maior prioridade (menor `priority`) entre as
/// PRONTAS (todo id em `depends_on` já com `passes == true`); desempate por `id`. `None`
/// quando não há pendência pronta — pode significar fim de fato (nenhuma pendência) ou
/// dependências bloqueadas.
pub fn next_pending() -> Option<Feature> {
    let features = load();
    let passed: HashSet<i32> = features.iter().filter(|f| f.passes).map(|f| f.id).collect();

    features
        .into_iter()
        .filter(|f| !f.passes && f.depends_on.iter().all(|d| passed.contains(d)))
        .min_by_key(|f| (f.priority, f.id))
}

/// Marca a feature como concluída e regrava a lista. No-op se o id não existe.
pub fn mark_passed(id: i32) {
    let mut features = load();
    if !features.iter().any(|f| f.id == id) {
        return;
    }
    for f in &mut features {
        if f.id == id {
            f.passes = true;
        }
    }
    write(&features);
}

/// Quantas features ainda faltam (`passes == false`).
pub fn pending_count() -> usize {
    load().iter().filter(|f| !f.passes).count()
}

/// Há features e todas passaram — condição de término do loop.
pub fn all_passing() -> bool {
    let features = load();
    !features.is_empty() && features.iter().all(|f| f.passes)
}

/// Apaga a lista do run anterior — o flow PRODUTOR reseta no seu `start`.
pub fn reset() {
    let p = std::path::Path::new(FILE_PATH);
    if p.exists() {
        if let Err(e) = std::fs::remove_file(p) {
            eprintln!("[FeatureStore] falha ao limpar: {e}");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support::lock_cwd;

    struct Isolated {
        _dir: tempfile::TempDir,
        previous: std::path::PathBuf,
    }

    impl Isolated {
        fn new() -> Self {
            let dir = tempfile::tempdir().unwrap();
            let previous = std::env::current_dir().unwrap();
            std::env::set_current_dir(dir.path()).unwrap();
            Self {
                _dir: dir,
                previous,
            }
        }
    }

    impl Drop for Isolated {
        fn drop(&mut self) {
            let _ = std::env::set_current_dir(&self.previous);
        }
    }

    fn feature(id: i32, title: &str, priority: i32, passes: bool) -> Feature {
        Feature {
            id,
            title: title.to_string(),
            priority,
            passes,
            depends_on: Vec::new(),
        }
    }

    fn feature_dep(id: i32, title: &str, priority: i32, passes: bool, deps: Vec<i32>) -> Feature {
        Feature {
            depends_on: deps,
            ..feature(id, title, priority, passes)
        }
    }

    #[test]
    fn write_e_load_fazem_roundtrip() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write(&[feature(1, "A", 2, false), feature(2, "B", 1, false)]);

        let loaded = load();

        assert_eq!(loaded.len(), 2);
        assert_eq!(loaded[0].title, "A");
    }

    #[test]
    fn write_formata_json_para_leitura() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write(&[feature(1, "A", 2, false)]);

        let json = std::fs::read_to_string(FILE_PATH).unwrap();
        assert!(json.contains('\n'));
        assert!(json.contains("  \"items\": ["));
        assert!(json.contains("      \"title\": \"A\""));
    }

    #[test]
    fn parse_array_cru_forca_pendente_e_preserva_campos() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let features = parse(
            r#"[{"id":1,"title":"Login","priority":1},{"id":2,"title":"Logout","priority":3}]"#,
        );

        assert_eq!(features.len(), 2);
        assert!(features.iter().all(|f| !f.passes));
        assert_eq!(features[0].title, "Login");
    }

    #[test]
    fn parse_sem_id_reindexa() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let features = parse(r#"[{"title":"X","priority":1},{"title":"Y","priority":1}]"#);

        assert_eq!(
            features.iter().map(|f| f.id).collect::<Vec<_>>(),
            vec![1, 2]
        );
    }

    #[test]
    fn parse_json_invalido_retorna_vazio_sem_lancar() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert!(parse("isso não é json").is_empty());
        assert!(parse("[]").is_empty());
    }

    #[test]
    fn next_pending_escolhe_maior_prioridade_pendente() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write(&[
            feature(1, "baixa", 3, false),
            feature(2, "alta", 1, false),
            feature(3, "media", 2, true), // já passa — ignorada
        ]);

        assert_eq!(next_pending().unwrap().id, 2);
    }

    #[test]
    fn parse_depends_on_ausente_normaliza_para_array_vazio() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let features = parse(r#"[{"id":1,"title":"X","priority":1}]"#);

        assert!(features[0].depends_on.is_empty());
    }

    #[test]
    fn parse_depends_on_ciclico_retorna_vazio_sem_lancar() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let features = parse(
            r#"[{"id":1,"title":"A","priority":1,"dependsOn":[2]},{"id":2,"title":"B","priority":2,"dependsOn":[1]}]"#,
        );

        assert!(features.is_empty());
    }

    #[test]
    fn parse_depends_on_auto_referencia_retorna_vazio() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let features = parse(r#"[{"id":1,"title":"A","priority":1,"dependsOn":[1]}]"#);

        assert!(features.is_empty());
    }

    #[test]
    fn parse_depends_on_id_inexistente_retorna_vazio() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let features = parse(r#"[{"id":1,"title":"A","priority":1,"dependsOn":[99]}]"#);

        assert!(features.is_empty());
    }

    #[test]
    fn load_feature_list_legado_sem_depends_on_nao_lanca() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        // Simula um feature_list.json gravado por uma versão anterior do harness, sem a
        // chave "dependsOn" — prova a compatibilidade retroativa.
        std::fs::create_dir_all(".harness").unwrap();
        std::fs::write(
            ".harness/feature_list.json",
            r#"{"items":[{"id":1,"title":"A","priority":1,"passes":false}]}"#,
        )
        .unwrap();

        let loaded = load();

        assert_eq!(loaded.len(), 1);
        assert!(loaded[0].depends_on.is_empty());
    }

    #[test]
    fn next_pending_ignora_feature_com_dependencia_pendente() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write(&[
            feature(1, "fundação", 2, false),
            feature_dep(2, "depende de 1", 1, false, vec![1]), // prioridade "melhor", mas bloqueada
        ]);

        assert_eq!(next_pending().unwrap().id, 1);
    }

    #[test]
    fn next_pending_libera_feature_apos_dependencia_passar() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write(&[
            feature(1, "fundação", 2, false),
            feature_dep(2, "depende de 1", 1, false, vec![1]),
        ]);
        assert_eq!(next_pending().unwrap().id, 1);

        mark_passed(1);

        assert_eq!(next_pending().unwrap().id, 2);
    }

    #[test]
    fn next_pending_todas_bloqueadas_retorna_none_com_pendencias_existentes() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        // Grafo cíclico gravado direto via write (bypassando a validação de parse) —
        // simula um feature_list.json editado à mão fora do fluxo normal.
        write(&[
            feature_dep(1, "A", 1, false, vec![2]),
            feature_dep(2, "B", 2, false, vec![1]),
        ]);

        assert!(next_pending().is_none());
        assert_eq!(pending_count(), 2);
    }

    #[test]
    fn mark_passed_vira_a_feature_e_all_passing_fecha_quando_todas_passam() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write(&[feature(1, "A", 1, false), feature(2, "B", 2, false)]);

        mark_passed(1);
        assert_eq!(pending_count(), 1);
        assert!(!all_passing());

        mark_passed(2);
        assert_eq!(pending_count(), 0);
        assert!(all_passing());
        assert!(next_pending().is_none());
    }

    #[test]
    fn all_passing_lista_vazia_e_falso() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert!(!all_passing());
    }

    #[test]
    fn reset_apaga_a_lista() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write(&[feature(1, "A", 1, false)]);
        reset();

        assert!(load().is_empty());
    }
}
