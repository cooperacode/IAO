//! Output template for an artifact: `.harness/skills/<name>/ARTIFACT.md` with `{{key}}`
//! placeholders replaced by values from `state_store`. The artifact's markdown shape
//! lives alongside the skill that produces it — outside the code, editable without a
//! recompile. Pure string substitution: deterministic, zero token cost.

use std::collections::HashMap;

use crate::{harness_log, path_resolver};

/// Reads the skill's template; `None` if the skill doesn't define one (the caller decides the fallback).
pub fn load(skill_name: &str) -> Option<String> {
    let rel_path = format!(".harness/skills/{skill_name}/ARTIFACT.md");
    let path = path_resolver::resolve(&rel_path);
    let path = std::path::Path::new(&path);
    if !path.exists() {
        return None;
    }
    match std::fs::read_to_string(path) {
        Ok(content) => Some(content),
        Err(e) => {
            harness_log::error(&format!("[ArtifactTemplate] failed to read template for {skill_name}: {e}"));
            None
        }
    }
}

/// Replaces each `{{key}}` with its corresponding value. Placeholders with no value
/// remain in the text — a visible sign of missing data, not a silent error.
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
            ("title".to_string(), "Risks".to_string()),
            ("body".to_string(), "list".to_string()),
        ]);

        let result = render("# {{title}}\n\n{{body}}\n\n{{no_value}}", &values);

        assert!(result.contains("# Risks"));
        assert!(result.contains("list"));
        assert!(result.contains("{{no_value}}")); // missing data stays visible, doesn't vanish
    }

    #[test]
    fn load_skill_sem_template_retorna_none() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert!(load("skill-without-artifact").is_none());
    }

    #[test]
    fn load_le_o_template_da_skill() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        std::fs::create_dir_all(".harness/skills/my-skill").unwrap();
        std::fs::write(".harness/skills/my-skill/ARTIFACT.md", "# {{title}}").unwrap();

        assert_eq!(load("my-skill").unwrap(), "# {{title}}");
    }
}
