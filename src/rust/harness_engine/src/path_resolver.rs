//! Resolves paths relative to the working directory (repo root, from where the driver
//! invokes the harness), falling back to the binary's directory. Shared by anything that
//! injects files into the prompt (skills, docs).

use std::path::PathBuf;

pub fn resolve(path: &str) -> String {
    let trimmed = path.trim();
    if PathBuf::from(trimmed).is_absolute() {
        return trimmed.to_string();
    }

    if let Ok(cwd) = std::env::current_dir() {
        let from_cwd = cwd.join(trimmed);
        if from_cwd.exists() {
            let canonical = from_cwd.canonicalize().unwrap_or_else(|_| from_cwd.clone());
            let canonical_cwd = cwd.canonicalize().unwrap_or(cwd);
            // Compares paths that are ALREADY canonicalized: a symlink inside the CWD can
            // point outside of it, and `canonicalize()` follows the link without warning.
            // If the real result escaped the base, treat it as not found and fall back to
            // the binary — full containment against a signed policy root (capability
            // broker) is future-phase work (RFC §6.3); this is just the minimal rejection
            // of a symlink escape.
            if canonical.starts_with(&canonical_cwd) {
                return canonical.to_string_lossy().to_string();
            }
        }
    }

    let base_dir = std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|p| p.to_path_buf()))
        .unwrap_or_else(|| std::env::current_dir().unwrap_or_default());
    let base_dir = base_dir.canonicalize().unwrap_or(base_dir);

    base_dir.join(trimmed).to_string_lossy().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support::lock_cwd;

    #[test]
    fn resolve_caminho_absoluto_retorna_o_mesmo_caminho() {
        let _guard = lock_cwd();
        let dir = tempfile::tempdir().unwrap();
        let absolute = dir.path().join("harness.json");

        assert_eq!(
            resolve(absolute.to_str().unwrap()),
            absolute.to_str().unwrap()
        );
    }

    #[test]
    fn resolve_caminho_relativo_existente_resolve_a_partir_do_cwd() {
        let _guard = lock_cwd();
        let dir = tempfile::tempdir().unwrap();
        let previous = std::env::current_dir().unwrap();
        std::env::set_current_dir(dir.path()).unwrap();

        std::fs::write("harness.json", "{}").unwrap();
        let resolved = resolve("harness.json");

        std::env::set_current_dir(previous).unwrap();

        let expected = dir.path().join("harness.json").canonicalize().unwrap();
        assert_eq!(resolved, expected.to_string_lossy());
    }

    #[test]
    fn resolve_caminho_relativo_inexistente_cai_no_fallback_do_binario() {
        let _guard = lock_cwd();
        let dir = tempfile::tempdir().unwrap();
        let previous = std::env::current_dir().unwrap();
        std::env::set_current_dir(dir.path()).unwrap();

        // Not existing anywhere, it falls back to the path relative to the binary — never
        // panics, and doesn't get stuck on the test's cwd, which is exactly the case where
        // nothing exists there.
        let resolved = resolve("a-file-that-does-not-exist.md");

        std::env::set_current_dir(previous).unwrap();

        let exe_dir = std::env::current_exe()
            .unwrap()
            .parent()
            .unwrap()
            .canonicalize()
            .unwrap();
        let expected = exe_dir.join("a-file-that-does-not-exist.md");
        assert_eq!(resolved, expected.to_string_lossy());
    }

    #[cfg(unix)]
    #[test]
    fn resolve_symlink_que_escapa_do_cwd_nao_segue_o_link() {
        let _guard = lock_cwd();

        let outside = tempfile::tempdir().unwrap();
        let secret = outside.path().join("secret.txt");
        std::fs::write(&secret, "secret").unwrap();
        let secret_canonical = secret.canonicalize().unwrap();

        let cwd_dir = tempfile::tempdir().unwrap();
        let previous = std::env::current_dir().unwrap();
        std::env::set_current_dir(cwd_dir.path()).unwrap();

        std::os::unix::fs::symlink(&secret, "link.txt").unwrap();
        let resolved = resolve("link.txt");

        std::env::set_current_dir(previous).unwrap();

        // The link exists and points outside the CWD — it must not be returned as the
        // real path it resolves to (that would leak the escape); falls back to the binary.
        assert_ne!(resolved, secret_canonical.to_string_lossy());
    }
}
