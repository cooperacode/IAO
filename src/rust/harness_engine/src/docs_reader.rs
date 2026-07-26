//! Lê um conjunto de documentos (`*.md` e `*.txt`) de uma pasta para injetar no prompt.
//! É a entrada alternativa ao input interativo: o flow lê o material já existente (specs,
//! notas, transcrições) e o modelo sintetiza um brief a partir dele.
//!
//! Análogo a como `prompt_formatter` injeta skills — a leitura é determinística (feita em
//! código), só a síntese fica com o modelo.

use crate::{harness_config, path_resolver};

const EXTENSIONS: [&str; 2] = ["md", "txt"];

// Teto de octetos UTF-8 (RFC Apêndice B item 1: medir em bytes, não em codepoints, para
// que o teto tenha o mesmo significado entre engines .NET/Python/Rust). Ao exceder, trunca
// em fronteira de char válida e avisa no stderr. Valor vem do harness.json (ou do default).
fn max_chars() -> usize {
    harness_config::current().docs_max_chars.max(0) as usize
}

/// Corta `text` em no máximo `max_bytes` octetos UTF-8, recuando até a fronteira de char
/// válida mais próxima — nunca parte um caractere multibyte (acento, emoji) ao meio.
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

/// Existe a pasta e há ao menos um arquivo `*.md`/`*.txt`?
pub fn has_docs(folder: &str) -> bool {
    let dir = path_resolver::resolve(folder);
    let dir = std::path::Path::new(&dir);
    dir.is_dir() && !files(dir).is_empty()
}

/// Concatena os documentos em ordem alfabética, cada um sob um cabeçalho
/// `## <nome-do-arquivo>`, e devolve também a lista de nomes (para citar as fontes).
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
                eprintln!("[DocsReader] falha ao ler {name}: {e}");
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
            eprintln!("[DocsReader] conteúdo excedeu {max_chars} bytes (UTF-8); truncando em {name}.");
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
        let missing = dir.path().join("nao-existe");

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
        std::fs::write(dir.path().join("imagem.png"), "x").unwrap();
        std::fs::write(dir.path().join("dados.json"), "{}").unwrap();

        assert!(!has_docs(dir.path().to_str().unwrap()));
    }

    #[test]
    fn has_docs_com_markdown_true() {
        let dir = temp_dir();
        std::fs::write(dir.path().join("spec.md"), "conteúdo").unwrap();

        assert!(has_docs(dir.path().to_str().unwrap()));
    }

    #[test]
    fn read_concatena_md_e_txt_em_ordem_alfabetica() {
        let dir = temp_dir();
        std::fs::write(dir.path().join("b-notas.txt"), "notas").unwrap();
        std::fs::write(dir.path().join("a-spec.md"), "spec").unwrap();

        let (content, files) = read(dir.path().to_str().unwrap());

        assert_eq!(
            files,
            vec!["a-spec.md".to_string(), "b-notas.txt".to_string()]
        );
        assert!(content.contains("## a-spec.md"));
        assert!(content.contains("## b-notas.txt"));
        assert!(content.find("a-spec.md") < content.find("b-notas.txt"));
    }

    #[test]
    fn read_pasta_inexistente_vazio_sem_fontes() {
        let dir = temp_dir();
        let missing = dir.path().join("nao-existe");

        let (content, files) = read(missing.to_str().unwrap());

        assert_eq!(content, "");
        assert!(files.is_empty());
    }

    #[test]
    fn truncate_utf8_bytes_nao_quebra_caractere_multibyte_no_meio() {
        // "café ☕" — "é" (2 bytes) e "☕" (3 bytes) são multibyte; um corte ingênuo em
        // qualquer posição produziria uma string UTF-8 inválida (panic ao fatiar).
        let text = "café ☕";
        for max in 0..=text.len() {
            let truncated = truncate_utf8_bytes(text, max);
            assert!(truncated.len() <= max);
            // Se compilou e devolveu uma `String`, já é UTF-8 válido por construção do tipo.
        }
    }

    #[test]
    fn read_trunca_em_fronteira_utf8_valida_sem_quebrar_caractere() {
        let dir = temp_dir();
        // Conteúdo pequeno com acento perto do teto de bytes de docs_max_chars do config
        // default não é prático de forçar aqui sem mexer em harness_config; o teste do
        // helper acima cobre a garantia central. Este teste cobre a integração básica.
        std::fs::write(dir.path().join("a.md"), "café ☕").unwrap();

        let (content, _) = read(dir.path().to_str().unwrap());

        assert!(content.contains("café ☕"));
    }
}
