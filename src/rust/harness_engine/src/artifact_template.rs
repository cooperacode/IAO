//! Template de saída de um artefato: `skills/<name>/ARTIFACT.md` com placeholders
//! `{{chave}}` substituídos por valores do `state_store`. A forma markdown do artefato
//! mora junto da skill que o produz — fora do código, editável sem recompilar.
//! Substituição pura de strings: determinística, zero token.

use std::collections::HashMap;

use crate::path_resolver;

/// Lê o template da skill; `None` se a skill não define um (o caller decide o fallback).
pub fn load(skill_name: &str) -> Option<String> {
    let rel_path = format!("skills/{skill_name}/ARTIFACT.md");
    let path = path_resolver::resolve(&rel_path);
    let path = std::path::Path::new(&path);
    if !path.exists() {
        return None;
    }
    match std::fs::read_to_string(path) {
        Ok(content) => Some(content),
        Err(e) => {
            eprintln!("[ArtifactTemplate] falha ao ler template de {skill_name}: {e}");
            None
        }
    }
}

/// Substitui cada `{{chave}}` pelo valor correspondente. Placeholders sem valor
/// permanecem no texto — sinal visível de dado faltante, não erro silencioso.
pub fn render(template: &str, values: &HashMap<String, String>) -> String {
    let mut result = template.to_string();
    for (key, value) in values {
        result = result.replace(&format!("{{{{{key}}}}}"), value);
    }
    result
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
    fn render_substitui_placeholders_e_mantem_os_desconhecidos() {
        let values = HashMap::from([
            ("titulo".to_string(), "Riscos".to_string()),
            ("corpo".to_string(), "lista".to_string()),
        ]);

        let result = render("# {{titulo}}\n\n{{corpo}}\n\n{{sem_valor}}", &values);

        assert!(result.contains("# Riscos"));
        assert!(result.contains("lista"));
        assert!(result.contains("{{sem_valor}}")); // dado faltante fica visível, não some
    }

    #[test]
    fn load_skill_sem_template_retorna_none() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert!(load("skill-sem-artifact").is_none());
    }

    #[test]
    fn load_le_o_template_da_skill() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        std::fs::create_dir_all("skills/minha-skill").unwrap();
        std::fs::write("skills/minha-skill/ARTIFACT.md", "# {{titulo}}").unwrap();

        assert_eq!(load("minha-skill").unwrap(), "# {{titulo}}");
    }
}
