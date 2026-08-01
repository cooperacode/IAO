//! Assembles the instruction block (input/response/skills) delivered to the model.

use std::collections::HashMap;

use crate::envelope::Envelope;
use crate::{path_resolver, state_store};

pub fn skills(names: &[&str]) -> HashMap<String, String> {
    names
        .iter()
        .filter(|n| !n.trim().is_empty())
        .map(|n| (n.to_string(), format!(".harness/skills/{n}/SKILL.md")))
        .collect()
}

pub fn format(input: &str, output: &Envelope, skills: Option<&HashMap<String, String>>) -> String {
    // Reinjects the driver context (captured on `start`, see `task_registry`/
    // `state_store`) into every output — a single point, so no task needs to pass it
    // along manually.
    let enriched = if output.context.is_none() {
        Envelope {
            context: state_store::get_context(),
            ..output.clone()
        }
    } else {
        output.clone()
    };

    let skills_block = read_skills(skills);
    let response = enriched.to_json();

    format!(
        "Execute the instruction inside the `input` tag. Then reply with the result as JSON.\n\
\n\
Output contract — a reply that breaks any of these rules is invalid and wastes a retry:\n\
1. Output EXACTLY one JSON object, on a SINGLE line, matching the shape in the `response` tag with the placeholders replaced by real values.\n\
2. The object is the ONLY thing you output: no markdown code fences, no comments, no prose before or after it, nothing.\n\
3. Keep the same keys, types and nesting as the schema — do not add, remove, rename fields, or wrap the object in an array.\n\
4. Every value must be valid JSON: use only double quotes for strings, escape `\"` and `\\` inside them, and replace any line break inside a value with the literal characters `\\n` — never a raw newline. No trailing commas.\n\
5. Before answering, mentally re-parse your own output as JSON; if it would fail to parse, fix it before sending.\n\
\n\
{skills_block}\n\
<input>\n\
    {input}\n\
</input>\n\
<response>\n\
    {response}\n\
</response>"
    )
}

fn read_skills(skills: Option<&HashMap<String, String>>) -> String {
    let Some(skills) = skills else {
        return String::new();
    };

    // Sorted for determinism: `HashMap` doesn't guarantee stable iteration order across
    // runs (unlike Python's `dict`).
    let mut ids: Vec<&String> = skills.keys().collect();
    ids.sort();

    let mut body = String::new();
    for id in ids {
        let rel_path = &skills[id];
        if rel_path.trim().is_empty() {
            continue;
        }

        let path = path_resolver::resolve(rel_path);
        let path = std::path::Path::new(&path);
        if !path.exists() {
            continue;
        }

        let content = match std::fs::read_to_string(path) {
            Ok(c) => c,
            Err(_) => continue,
        };
        // Inline the content but preserve line breaks as literal "\n" markers
        let content = content.replace("\r\n", "\\n").replace('\n', "\\n");

        body.push_str(&format!("<skill id=\"{id}\">{content}</skill>"));
    }

    if body.is_empty() {
        String::new()
    } else {
        format!("<skills>\n    {body}\n</skills>")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::envelope_type;
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
    fn skills_aceita_varios_nomes_retorna_todos_os_mapeamentos() {
        let map = skills(&["agile-workitem", "story-splitting"]);

        assert_eq!(map.len(), 2);
        assert_eq!(map["agile-workitem"], ".harness/skills/agile-workitem/SKILL.md");
        assert_eq!(map["story-splitting"], ".harness/skills/story-splitting/SKILL.md");
    }

    #[test]
    fn format_contexto_persistido_e_reinjetado_no_envelope_de_saida() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        state_store::set_context(HashMap::from([(
            "driver".to_string(),
            "claude code".to_string(),
        )]));
        let output = Envelope::new(envelope_type::COMMAND, "plan", vec![]);

        let result = format("do something", &output, None);

        assert!(result.contains(r#""context":{"driver":"claude code"}"#));
    }

    #[test]
    fn format_sem_contexto_persistido_nao_emite_o_campo() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let output = Envelope::new(envelope_type::COMMAND, "plan", vec![]);

        let result = format("do something", &output, None);

        assert!(!result.contains("context"));
    }

    #[test]
    fn format_contexto_ja_definido_na_task_nao_e_sobrescrito() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        state_store::set_context(HashMap::from([(
            "driver".to_string(),
            "claude code".to_string(),
        )]));
        let mut output = Envelope::new(envelope_type::COMMAND, "plan", vec![]);
        output.context = Some(HashMap::from([(
            "driver".to_string(),
            "explicit".to_string(),
        )]));

        let result = format("do something", &output, None);

        assert!(result.contains("explicit"));
        assert!(!result.contains("claude code"));
    }
}
