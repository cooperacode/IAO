//! Persiste cada artefato do flow no seu próprio arquivo (`.harness/<nome>.md`) e mantém
//! um manifesto (`.harness/artifacts.json`) com a ordem de gravação. O manifesto é o
//! contrato entre produtor e consumidor: a avaliação lê os artefatos por ele, sem
//! depender de um relatório combinado.
//!
//! Só o flow PRODUTOR reseta o manifesto (no seu `start`) — o consumidor (avaliação) não
//! toca nele, pela mesma razão dos snapshots de `trace`/`state_store`: o start do
//! avaliador não pode apagar a evidência que ele mesmo vai ler.

use serde::{Deserialize, Serialize};

const DIR: &str = ".harness";
pub const MANIFEST_PATH: &str = ".harness/artifacts.json";

#[derive(Debug, Serialize, Deserialize)]
struct ArtifactManifest {
    #[serde(default)]
    files: Vec<String>,
}

/// Apaga os artefatos do run anterior e o manifesto — chamado pelo flow produtor no start.
pub fn reset() {
    for file in files() {
        let p = std::path::Path::new(&file);
        if p.exists() {
            if let Err(e) = std::fs::remove_file(p) {
                eprintln!("[ArtifactStore] falha ao limpar: {e}");
            }
        }
    }
    let manifest = std::path::Path::new(MANIFEST_PATH);
    if manifest.exists() {
        if let Err(e) = std::fs::remove_file(manifest) {
            eprintln!("[ArtifactStore] falha ao limpar: {e}");
        }
    }
}

/// Grava `.harness/<nome>.md` e registra o caminho no manifesto (uma vez, em ordem de
/// chegada).
pub fn write(name: &str, content: &str) -> String {
    let path = format!("{DIR}/{name}.md");

    if let Err(e) = std::fs::create_dir_all(DIR) {
        eprintln!("[ArtifactStore] falha ao gravar {name}: {e}");
        return path;
    }
    if let Err(e) = std::fs::write(&path, content) {
        eprintln!("[ArtifactStore] falha ao gravar {name}: {e}");
        return path;
    }

    let mut current_files = files();
    if !current_files.contains(&path) {
        current_files.push(path.clone());
        save_manifest(&current_files);
    }

    path
}

/// Caminhos registrados no manifesto, na ordem em que foram gravados.
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
            Err(e) => eprintln!("[ArtifactStore] falha ao carregar manifesto: {e}"),
        }
    }
    Vec::new()
}

/// Há artefatos gravados e presentes no disco?
pub fn has_artifacts() -> bool {
    files().iter().any(|f| std::path::Path::new(f).exists())
}

/// Concatena os artefatos na ordem do manifesto — o insumo do juiz-LLM.
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
                Err(e) => eprintln!("[ArtifactStore] falha ao ler {file}: {e}"),
            }
        }
    }

    parts.trim_end().to_string()
}

fn save_manifest(file_list: &[String]) {
    if let Err(e) = std::fs::create_dir_all(DIR) {
        eprintln!("[ArtifactStore] falha ao carregar manifesto: {e}");
        return;
    }
    let manifest = ArtifactManifest {
        files: file_list.to_vec(),
    };
    match serde_json::to_string(&manifest) {
        Ok(json) => {
            if let Err(e) = std::fs::write(MANIFEST_PATH, json) {
                eprintln!("[ArtifactStore] falha ao carregar manifesto: {e}");
            }
        }
        Err(e) => eprintln!("[ArtifactStore] falha ao carregar manifesto: {e}"),
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

        let path = write("historias", "# Histórias\n\n1. a");

        assert!(std::path::Path::new(&path).exists());
        assert_eq!(files(), vec![path]);
    }

    #[test]
    fn write_mesmo_nome_duas_vezes_sobrescreve_sem_duplicar_no_manifesto() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write("historias", "v1");
        let path = write("historias", "v2");

        assert_eq!(files().len(), 1);
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "v2");
    }

    #[test]
    fn read_all_concatena_na_ordem_de_gravacao() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write("item", "# Item");
        write("historias", "# Histórias");

        let all = read_all();

        assert!(all.find("# Item") < all.find("# Histórias"));
    }

    #[test]
    fn reset_apaga_artefatos_e_manifesto() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let path = write("historias", "x");

        reset();

        assert!(!std::path::Path::new(&path).exists());
        assert!(!has_artifacts());
        assert!(files().is_empty());
    }
}
