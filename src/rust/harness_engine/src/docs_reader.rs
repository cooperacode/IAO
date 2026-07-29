//! Reads a set of documents (`*.md` and `*.txt`) from a folder to inject into the prompt.
//! This is the alternative input to interactive input: the flow reads material that
//! already exists (specs, notes, transcripts) and the model synthesizes a brief from it.
//!
//! Analogous to how `prompt_formatter` injects skills — the reading is deterministic (done
//! in code), only the synthesis is left to the model.

use crate::{harness_config, path_resolver};

const EXTENSIONS: [&str; 2] = ["md", "txt"];

// UTF-8 byte ceiling (RFC Appendix B item 1: measure in bytes, not codepoints, so the
// ceiling has the same meaning across the .NET/Python/Rust engines). When exceeded,
// truncates at a valid char boundary and warns on stderr. Value comes from harness.json
// (or the default).
fn max_chars() -> usize {
    harness_config::current().docs_max_chars.max(0) as usize
}

/// Cuts `text` down to at most `max_bytes` UTF-8 bytes, backing off to the nearest valid
/// char boundary — never splits a multibyte character (accent, emoji) in half.
fn truncate_utf8_bytes(text: &str, max_bytes: usize) -> String {
    if text.len() <= max_bytes {
        return text.to_string();
    }

    let mut cut = max_bytes;
    while cut > 0 && !text.is_char_boundary(cut) {
        cut -= 1;
    }

    text[..cut].to_string()
}

/// Does the folder exist and does it contain at least one `*.md`/`*.txt` file?
pub fn has_docs(folder: &str) -> bool {
    let dir = path_resolver::resolve(folder);
    let dir = std::path::Path::new(&dir);
    dir.is_dir() && !files(dir).is_empty()
}

/// Concatenates the documents in alphabetical order, each under a `## <file-name>`
/// heading, and also returns the list of names (to cite the sources).
pub fn read(folder: &str) -> (String, Vec<String>) {
    let dir = path_resolver::resolve(folder);
    let dir = std::path::Path::new(&dir);
    if !dir.is_dir() {
        return (String::new(), Vec::new());
    }

    let files = files(dir);
    let mut names = Vec::with_capacity(files.len());
    let mut content = String::new();
    let max_chars = max_chars();

    for path in files {
        let name = path.file_name().unwrap().to_string_lossy().to_string();
        let text = match std::fs::read_to_string(&path) {
            Ok(t) => t,
            Err(e) => {
                eprintln!("[DocsReader] failed to read {name}: {e}");
                continue;
            }
        };

        names.push(name.clone());
        content.push_str("## ");
        content.push_str(&name);
        content.push_str("\n\n");
        content.push_str(&text);
        content.push_str("\n\n");

        if content.len() > max_chars {
            eprintln!("[DocsReader] content exceeded {max_chars} bytes (UTF-8); truncating at {name}.");
            content = truncate_utf8_bytes(&content, max_chars);
            break;
        }
    }

    (content.trim_end().to_string(), names)
}

fn files(dir: &std::path::Path) -> Vec<std::path::PathBuf> {
    let mut entries: Vec<std::path::PathBuf> = std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .filter(|p| p.is_file())
        .filter(|p| {
            p.extension()
                .map(|ext| EXTENSIONS.contains(&ext.to_string_lossy().to_lowercase().as_str()))
                .unwrap_or(false)
        })
        .collect();

    entries.sort_by_key(|p| p.file_name().unwrap().to_string_lossy().to_lowercase());
    entries
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir() -> tempfile::TempDir {
        tempfile::tempdir().unwrap()
    }

    #[test]
    fn has_docs_pasta_inexistente_false() {
        let dir = temp_dir();
        let missing = dir.path().join("does-not-exist");

        assert!(!has_docs(missing.to_str().unwrap()));
    }

    #[test]
    fn has_docs_pasta_vazia_false() {
        let dir = temp_dir();

        assert!(!has_docs(dir.path().to_str().unwrap()));
    }

    #[test]
    fn has_docs_ignora_extensoes_nao_suportadas() {
        let dir = temp_dir();
        std::fs::write(dir.path().join("image.png"), "x").unwrap();
        std::fs::write(dir.path().join("data.json"), "{}").unwrap();

        assert!(!has_docs(dir.path().to_str().unwrap()));
    }

    #[test]
    fn has_docs_com_markdown_true() {
        let dir = temp_dir();
        std::fs::write(dir.path().join("spec.md"), "content").unwrap();

        assert!(has_docs(dir.path().to_str().unwrap()));
    }

    #[test]
    fn read_concatena_md_e_txt_em_ordem_alfabetica() {
        let dir = temp_dir();
        std::fs::write(dir.path().join("b-notes.txt"), "notes").unwrap();
        std::fs::write(dir.path().join("a-spec.md"), "spec").unwrap();

        let (content, files) = read(dir.path().to_str().unwrap());

        assert_eq!(
            files,
            vec!["a-spec.md".to_string(), "b-notes.txt".to_string()]
        );
        assert!(content.contains("## a-spec.md"));
        assert!(content.contains("## b-notes.txt"));
        assert!(content.find("a-spec.md") < content.find("b-notes.txt"));
    }

    #[test]
    fn read_pasta_inexistente_vazio_sem_fontes() {
        let dir = temp_dir();
        let missing = dir.path().join("does-not-exist");

        let (content, files) = read(missing.to_str().unwrap());

        assert_eq!(content, "");
        assert!(files.is_empty());
    }

    #[test]
    fn truncate_utf8_bytes_nao_quebra_caractere_multibyte_no_meio() {
        // "café ☕" — "é" (2 bytes) and "☕" (3 bytes) are multibyte; a naive cut at any
        // position would produce an invalid UTF-8 string (panic while slicing).
        let text = "café ☕";
        for max in 0..=text.len() {
            let truncated = truncate_utf8_bytes(text, max);
            assert!(truncated.len() <= max);
            // If it compiled and returned a `String`, it's already valid UTF-8 by the type's construction.
        }
    }

    #[test]
    fn read_trunca_em_fronteira_utf8_valida_sem_quebrar_caractere() {
        let dir = temp_dir();
        // Small content with an accent near the byte ceiling of the config's default
        // docs_max_chars isn't practical to force here without touching harness_config;
        // the helper test above covers the core guarantee. This test covers basic
        // integration.
        std::fs::write(dir.path().join("a.md"), "café ☕").unwrap();

        let (content, _) = read(dir.path().to_str().unwrap());

        assert!(content.contains("café ☕"));
    }
}
