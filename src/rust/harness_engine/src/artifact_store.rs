//! Persists each flow artifact in its own file (`.harness/<name>.md`) and keeps a
//! manifest (`.harness/artifacts.json`) with the write order. The manifest is the
//! contract between producer and consumer: the evaluation reads artifacts through it,
//! without depending on a combined report.
//!
//! Only the PRODUCER flow resets the manifest (on its `start`) — the consumer
//! (evaluation) doesn't touch it, for the same reason as the `trace`/`state_store`
//! snapshots: the evaluator's start must not erase the evidence it's about to read.

use serde::{Deserialize, Serialize};

use crate::harness_log;

const DIR: &str = ".harness";
pub const MANIFEST_PATH: &str = ".harness/artifacts.json";

#[derive(Debug, Serialize, Deserialize)]
struct ArtifactManifest {
    #[serde(default)]
    files: Vec<String>,
}

/// Deletes the previous run's artifacts and the manifest — called by the producer flow on start.
pub fn reset() {
    for file in files() {
        let p = std::path::Path::new(&file);
        if p.exists() {
            if let Err(e) = std::fs::remove_file(p) {
                harness_log::error(&format!("[ArtifactStore] failed to clear: {e}"));
            }
        }
    }
    let manifest = std::path::Path::new(MANIFEST_PATH);
    if manifest.exists() {
        if let Err(e) = std::fs::remove_file(manifest) {
            harness_log::error(&format!("[ArtifactStore] failed to clear: {e}"));
        }
    }
}

/// Writes `.harness/<name>.md` and registers the path in the manifest (once, in arrival order).
pub fn write(name: &str, content: &str) -> String {
    let path = format!("{DIR}/{name}.md");

    if let Err(e) = std::fs::create_dir_all(DIR) {
        harness_log::error(&format!("[ArtifactStore] failed to write {name}: {e}"));
        return path;
    }
    if let Err(e) = crate::atomic_io::write_atomic(std::path::Path::new(&path), content) {
        harness_log::error(&format!("[ArtifactStore] failed to write {name}: {e}"));
        return path;
    }

    let mut current_files = files();
    if !current_files.contains(&path) {
        current_files.push(path.clone());
        save_manifest(&current_files);
    }

    path
}

/// Reads a single artifact by name (e.g. for reinjection into prompts). "" if missing/unreadable.
pub fn read(name: &str) -> String {
    let path = format!("{DIR}/{name}.md");
    let p = std::path::Path::new(&path);

    if p.exists() {
        match std::fs::read_to_string(p) {
            Ok(content) => return content,
            Err(e) => harness_log::error(&format!("[ArtifactStore] failed to read {name}: {e}")),
        }
    }

    String::new()
}

/// Paths registered in the manifest, in the order they were written.
pub fn files() -> Vec<String> {
    let p = std::path::Path::new(MANIFEST_PATH);
    if p.exists() {
        let loaded = std::fs::read_to_string(p)
            .map_err(|e| e.to_string())
            .and_then(|json| {
                serde_json::from_str::<ArtifactManifest>(&json).map_err(|e| e.to_string())
            });

        match loaded {
            Ok(manifest) => return manifest.files,
            Err(e) => harness_log::error(&format!("[ArtifactStore] failed to load manifest: {e}")),
        }
    }
    Vec::new()
}

/// Are there artifacts registered and present on disk?
pub fn has_artifacts() -> bool {
    files().iter().any(|f| std::path::Path::new(f).exists())
}

/// Concatenates the artifacts in manifest order — the input to the LLM judge.
pub fn read_all() -> String {
    let mut parts = String::new();

    for file in files() {
        let p = std::path::Path::new(&file);
        if p.exists() {
            match std::fs::read_to_string(p) {
                Ok(content) => {
                    parts.push_str(content.trim_end());
                    parts.push('\n');
                }
                Err(e) => harness_log::error(&format!("[ArtifactStore] failed to read {file}: {e}")),
            }
        }
    }

    parts.trim_end().to_string()
}

fn save_manifest(file_list: &[String]) {
    if let Err(e) = std::fs::create_dir_all(DIR) {
        harness_log::error(&format!("[ArtifactStore] failed to load manifest: {e}"));
        return;
    }
    let manifest = ArtifactManifest {
        files: file_list.to_vec(),
    };
    match serde_json::to_string(&manifest) {
        Ok(json) => {
            if let Err(e) =
                crate::atomic_io::write_atomic(std::path::Path::new(MANIFEST_PATH), &json)
            {
                harness_log::error(&format!("[ArtifactStore] failed to load manifest: {e}"));
            }
        }
        Err(e) => harness_log::error(&format!("[ArtifactStore] failed to load manifest: {e}")),
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

    #[test]
    fn write_grava_o_arquivo_e_registra_no_manifesto() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let path = write("stories", "# Stories\n\n1. a");

        assert!(std::path::Path::new(&path).exists());
        assert_eq!(files(), vec![path]);
    }

    #[test]
    fn write_mesmo_nome_duas_vezes_sobrescreve_sem_duplicar_no_manifesto() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write("stories", "v1");
        let path = write("stories", "v2");

        assert_eq!(files().len(), 1);
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "v2");
    }

    #[test]
    fn read_all_concatena_na_ordem_de_gravacao() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write("item", "# Item");
        write("stories", "# Stories");

        let all = read_all();

        assert!(all.find("# Item") < all.find("# Stories"));
    }

    #[test]
    fn read_devolve_conteudo_gravado() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write("brief", "# Brief\n\nBuild X.");

        assert_eq!(read("brief"), "# Brief\n\nBuild X.");
    }

    #[test]
    fn read_nome_inexistente_devolve_vazio() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert_eq!(read("never-written"), "");
    }

    #[test]
    fn reset_apaga_artefatos_e_manifesto() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let path = write("stories", "x");

        reset();

        assert!(!std::path::Path::new(&path).exists());
        assert!(!has_artifacts());
        assert!(files().is_empty());
    }
}
