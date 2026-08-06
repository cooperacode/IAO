//! The development flow's feature list, persisted at `.harness/feature_list.json` — the
//! "persistent artifact" that survives context hard resets: each session (one feature)
//! reads and writes here, without depending on the conversation history. All features are
//! born with `passes = false`; the flow works through them one at a time until none are
//! left pending.
//!
//! Same tolerance as the other stores: missing or unreadable → empty list, never brings
//! down the run.

use std::collections::{HashMap, HashSet, VecDeque};

use serde::{Deserialize, Deserializer, Serialize};

use crate::harness_log;

const DIR: &str = ".harness";
const FILE_PATH: &str = ".harness/feature_list.json";

/// Character ceiling for `Feature::description` — a defensive quota against a verbose
/// driver: the description is reinjected into the `implement` prompt for every feature, so
/// without a ceiling it would silently inflate the context of every future session.
pub const DESCRIPTION_MAX_CHARS: usize = 700;
/// Character ceiling for the inline implementation context persisted per feature.
pub const IMPLEMENTATION_CONTEXT_MAX_CHARS: usize = 4000;

/// Structured inline guidance carried into implementation.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct ImplementationContext {
    #[serde(default)]
    pub requirements: Vec<String>,
    #[serde(default)]
    pub constraints: Vec<String>,
    #[serde(default)]
    pub files: Vec<String>,
    #[serde(default)]
    pub acceptance: Vec<String>,
}

impl ImplementationContext {
    pub fn is_empty(&self) -> bool {
        self.requirements.is_empty() && self.constraints.is_empty() && self.files.is_empty() && self.acceptance.is_empty()
    }

    pub fn prompt_text(&self) -> String {
        fn format_items(label: &str, values: &[String]) -> String {
            let values = values.iter().map(|value| value.replace("\r\n", "\\n").replace('\n', "\\n")).collect::<Vec<_>>();
            format!("{label}: {}", values.join("; "))
        }
        [
            format_items("requirements", &self.requirements),
            format_items("constraints", &self.constraints),
            format_items("files", &self.files),
            format_items("acceptance", &self.acceptance),
        ].join("\\n")
    }
}

#[derive(Deserialize)]
#[serde(untagged)]
enum RawImplementationContext {
    Structured(ImplementationContext),
    Legacy(String),
}

fn deserialize_implementation_context<'de, D>(deserializer: D) -> Result<ImplementationContext, D::Error>
where
    D: Deserializer<'de>,
{
    match Option::<RawImplementationContext>::deserialize(deserializer)? {
        Some(RawImplementationContext::Structured(context)) => Ok(context),
        Some(RawImplementationContext::Legacy(value)) if !value.trim().is_empty() => Ok(ImplementationContext {
            requirements: vec![value],
            ..Default::default()
        }),
        _ => Ok(ImplementationContext::default()),
    }
}

/// A feature from the development backlog: priority (lower = higher), whether it already
/// passes, which others (by id) it depends on, a free-form description (up to
/// `DESCRIPTION_MAX_CHARS` characters, reinjected into the `implement` prompt), and
/// explicit reference codes from the brief (e.g. "RF-003"; empty when the brief cites
/// none).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Feature {
    pub id: i32,
    pub title: String,
    pub priority: i32,
    #[serde(default)]
    pub passes: bool,
    #[serde(rename = "dependsOn", default)]
    pub depends_on: Vec<i32>,
    #[serde(default)]
    pub description: String,
    #[serde(default)]
    pub references: Vec<String>,
    #[serde(rename = "implementationContext", default, deserialize_with = "deserialize_implementation_context")]
    pub implementation_context: ImplementationContext,
}

#[derive(Debug, Serialize, Deserialize)]
struct FeatureList {
    items: Vec<Feature>,
}

// Raw shape of the array the driver returns in `plan` — `id` is optional (reindexed by
// order when absent/<=0), and `passes` is not read from here: every feature is born pending.
#[derive(Debug, Deserialize)]
struct RawFeature {
    #[serde(default)]
    id: i32,
    title: String,
    priority: i32,
    #[serde(rename = "dependsOn", default)]
    depends_on: Vec<i32>,
    #[serde(default)]
    description: String,
    #[serde(default)]
    references: Vec<String>,
    #[serde(rename = "implementationContext", default, deserialize_with = "deserialize_implementation_context")]
    implementation_context: ImplementationContext,
}

/// Overwrites the entire list — used by `plan` (session 0) and by `mark_passed`.
pub fn write(features: &[Feature]) {
    if let Err(e) = std::fs::create_dir_all(DIR) {
        harness_log::error(&format!("[FeatureStore] failed to write: {e}"));
        return;
    }
    let list = FeatureList {
        items: features.to_vec(),
    };
    match serde_json::to_string_pretty(&list) {
        Ok(json) => {
            if let Err(e) = crate::atomic_io::write_atomic(std::path::Path::new(FILE_PATH), &json) {
                harness_log::error(&format!("[FeatureStore] failed to write: {e}"));
            }
        }
        Err(e) => harness_log::error(&format!("[FeatureStore] failed to write: {e}")),
    }
}

/// Parses the raw feature array the driver returns in `plan`
/// (`[{"id":1,"title":"...","priority":1}, ...]`). Forces `passes = false` (every feature
/// is born pending) and reindexes missing/duplicate ids by order. Empty list if the JSON
/// fails to parse — the caller re-issues the request (corrective loop), doesn't bring down
/// the run.
pub fn parse(json: &str) -> Vec<Feature> {
    let parsed: Vec<RawFeature> = match serde_json::from_str(json) {
        Ok(p) => p,
        Err(e) => {
            harness_log::error(&format!("[FeatureStore] failed to parse features: {e}"));
            return Vec::new();
        }
    };

    if parsed.is_empty() {
        return Vec::new();
    }

    let explicit: Vec<i32> = parsed.iter().filter(|f| f.id > 0).map(|f| f.id).collect();
    let explicit_set: HashSet<i32> = explicit.iter().copied().collect();
    if explicit.len() != explicit_set.len() {
        harness_log::error("[FeatureStore] failed to parse features: duplicate explicit feature id");
        return Vec::new();
    }
    let mut used = explicit_set;
    let mut next_id = 1;
    let reindexed: Vec<Feature> = parsed
        .into_iter()
        .enumerate()
        .map(|(_i, f)| {
            if f.title.trim().is_empty() || f.priority <= 0 { return None; }
            let id = if f.id > 0 { f.id } else { while used.contains(&next_id) { next_id += 1; } used.insert(next_id); let id = next_id; next_id += 1; id };
            Some(Feature {
            id,
            title: f.title,
            priority: f.priority,
            passes: false,
            depends_on: unique_i32(f.depends_on),
            description: truncate_description(&f.description),
            references: unique_strings(f.references),
            implementation_context: truncate_implementation_context(&f.implementation_context),
        })})
        .collect::<Option<Vec<_>>>()
        .unwrap_or_default();

    if let Some(error) = dependency_graph_error(&reindexed) {
        harness_log::error(&format!("[FeatureStore] invalid dependency graph: {error}"));
        return Vec::new();
    }

    reindexed
}

fn unique_i32(values: Vec<i32>) -> Vec<i32> {
    let mut seen = HashSet::new();
    values.into_iter().filter(|v| seen.insert(*v)).collect()
}

fn unique_strings(values: Vec<String>) -> Vec<String> {
    let mut seen = HashSet::new();
    values.into_iter().filter(|v| !v.trim().is_empty() && seen.insert(v.clone())).collect()
}

// Cuts down to DESCRIPTION_MAX_CHARS characters — never throws, never rejects the whole
// feature because of it, only shortens.
fn truncate_description(description: &str) -> String {
    if description.chars().count() > DESCRIPTION_MAX_CHARS {
        description.chars().take(DESCRIPTION_MAX_CHARS).collect()
    } else {
        description.to_string()
    }
}

fn truncate_implementation_context(context: &ImplementationContext) -> ImplementationContext {
    let mut remaining = IMPLEMENTATION_CONTEXT_MAX_CHARS;
    let mut take = |values: &[String]| {
        let mut result = Vec::new();
        for value in values {
            if remaining == 0 { break; }
            if value.trim().is_empty() { continue; }
            let taken: String = value.chars().take(remaining).collect();
            remaining -= taken.chars().count();
            result.push(taken);
        }
        result
    };
    ImplementationContext {
        requirements: take(&context.requirements),
        constraints: take(&context.constraints),
        files: take(&context.files),
        acceptance: take(&context.acceptance),
    }
}

// `None` if the `depends_on` graph is valid (every id exists, no cycle); otherwise, a
// description of the problem. Kahn's algorithm (topological sort): a node left outside the
// resolved set ⇒ cycle. Checks for dangling refs first — otherwise a phantom dependency
// would be counted as eternally unresolved and reported as a "cycle" when it's actually an
// invalid id.
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
            "dependsOn references nonexistent id(s): {}",
            dangling.join(", ")
        ));
    }

    // i32 (not usize) on purpose: duplicate ids aren't deduplicated today by the reindex
    // above, so indegree may get decremented more times than expected — must not panic on
    // underflow in that edge case, just fail to close the cycle.
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
        Some(format!("cyclic dependency among features: {cyclic}"))
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
            harness_log::error(&format!("[FeatureStore] failed to load: {e}"));
            Vec::new()
        }
    }
}

/// The next feature to implement: the highest-priority one (lowest `priority`) among the
/// READY ones (every id in `depends_on` already has `passes == true`); ties broken by
/// `id`. `None` when there's no ready pending feature — this can mean either it's truly
/// done (no pending features) or dependencies are blocked.
pub fn next_pending() -> Option<Feature> {
    let features = load();
    let passed: HashSet<i32> = features.iter().filter(|f| f.passes).map(|f| f.id).collect();

    features
        .into_iter()
        .filter(|f| !f.passes && f.depends_on.iter().all(|d| passed.contains(d)))
        .min_by_key(|f| (f.priority, f.id))
}

/// Marks the feature as done and rewrites the list. No-op if the id doesn't exist.
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

/// How many features are still remaining (`passes == false`).
pub fn pending_count() -> usize {
    load().iter().filter(|f| !f.passes).count()
}

/// There are features and all of them passed — the loop's termination condition.
pub fn all_passing() -> bool {
    let features = load();
    !features.is_empty() && features.iter().all(|f| f.passes)
}

/// Deletes the previous run's list — the PRODUCER flow resets it on its `start`.
pub fn reset() {
    let p = std::path::Path::new(FILE_PATH);
    if p.exists() {
        if let Err(e) = std::fs::remove_file(p) {
            harness_log::error(&format!("[FeatureStore] failed to clear: {e}"));
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
            description: String::new(),
            references: Vec::new(),
            implementation_context: ImplementationContext::default(),
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

        assert!(parse("this is not json").is_empty());
        assert!(parse("[]").is_empty());
    }

    #[test]
    fn next_pending_escolhe_maior_prioridade_pendente() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write(&[
            feature(1, "low", 3, false),
            feature(2, "high", 1, false),
            feature(3, "medium", 2, true), // already passes — ignored
        ]);

        assert_eq!(next_pending().unwrap().id, 2);
    }

    #[test]
    fn parse_description_e_references_ausentes_normalizam_para_vazio() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let features = parse(r#"[{"id":1,"title":"X","priority":1}]"#);

        assert_eq!(features[0].description, "");
        assert!(features[0].references.is_empty());
        assert!(features[0].implementation_context.is_empty());
    }

    #[test]
    fn parse_preserva_description_e_references() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let features = parse(
            r#"[{"id":1,"title":"X","priority":1,"description":"does Y","references":["RF-003"],"implementationContext":"inline Y"}]"#,
        );

        assert_eq!(features[0].description, "does Y");
        assert_eq!(features[0].references, vec!["RF-003".to_string()]);
        assert_eq!(features[0].implementation_context.requirements, vec!["inline Y"]);
    }

    #[test]
    fn parse_description_acima_do_teto_e_truncada() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let long_description = "a".repeat(DESCRIPTION_MAX_CHARS + 50);

        let features = parse(&format!(
            r#"[{{"id":1,"title":"X","priority":1,"description":"{long_description}"}}]"#
        ));

        assert_eq!(features[0].description.chars().count(), DESCRIPTION_MAX_CHARS);
    }

    #[test]
    fn parse_implementation_context_acima_do_teto_e_truncado() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let long_context = "a".repeat(IMPLEMENTATION_CONTEXT_MAX_CHARS + 50);

        let features = parse(&format!(
            r#"[{{"id":1,"title":"X","priority":1,"implementationContext":"{long_context}"}}]"#
        ));

        assert_eq!(
            features[0].implementation_context.requirements[0].chars().count(),
            IMPLEMENTATION_CONTEXT_MAX_CHARS
        );
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

        // Simulates a feature_list.json written by an earlier version of the harness,
        // without the "dependsOn" key — proves backward compatibility.
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
            feature(1, "foundation", 2, false),
            feature_dep(2, "depends on 1", 1, false, vec![1]), // "better" priority, but blocked
        ]);

        assert_eq!(next_pending().unwrap().id, 1);
    }

    #[test]
    fn next_pending_libera_feature_apos_dependencia_passar() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write(&[
            feature(1, "foundation", 2, false),
            feature_dep(2, "depends on 1", 1, false, vec![1]),
        ]);
        assert_eq!(next_pending().unwrap().id, 1);

        mark_passed(1);

        assert_eq!(next_pending().unwrap().id, 2);
    }

    #[test]
    fn next_pending_todas_bloqueadas_retorna_none_com_pendencias_existentes() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        // Cyclic graph written directly via write (bypassing parse's validation) —
        // simulates a feature_list.json hand-edited outside the normal flow.
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
