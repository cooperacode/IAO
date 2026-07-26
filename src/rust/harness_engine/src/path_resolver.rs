//! Resolve caminhos relativos ao diretório de trabalho (raiz do repo, de onde o driver
//! invoca o harness), com fallback para o diretório do binário. Compartilhado por quem
//! injeta arquivos no prompt (skills, docs).

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
            // Compara caminhos JÁ canonicalizados: um symlink dentro do CWD pode apontar
            // para fora dele, e `canonicalize()` segue o link sem avisar. Se o resultado
            // real escapou da base, trata como não encontrado e cai no fallback do
            // binário — containment completo contra uma raiz de política assinada
            // (capability broker) é trabalho de fase futura (RFC §6.3); isto é só a
            // rejeição mínima de escape por symlink.
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

        // Não existindo em lugar nenhum, cai no fallback relativo ao binário — nunca
        // panica, e não fica preso ao cwd do teste, que é justamente o caso em que nada
        // existe lá.
        let resolved = resolve("um-arquivo-que-nao-existe.md");

        std::env::set_current_dir(previous).unwrap();

        let exe_dir = std::env::current_exe()
            .unwrap()
            .parent()
            .unwrap()
            .canonicalize()
            .unwrap();
        let expected = exe_dir.join("um-arquivo-que-nao-existe.md");
        assert_eq!(resolved, expected.to_string_lossy());
    }

    #[cfg(unix)]
    #[test]
    fn resolve_symlink_que_escapa_do_cwd_nao_segue_o_link() {
        let _guard = lock_cwd();

        let outside = tempfile::tempdir().unwrap();
        let secret = outside.path().join("secreto.txt");
        std::fs::write(&secret, "segredo").unwrap();
        let secret_canonical = secret.canonicalize().unwrap();

        let cwd_dir = tempfile::tempdir().unwrap();
        let previous = std::env::current_dir().unwrap();
        std::env::set_current_dir(cwd_dir.path()).unwrap();

        std::os::unix::fs::symlink(&secret, "link.txt").unwrap();
        let resolved = resolve("link.txt");

        std::env::set_current_dir(previous).unwrap();

        // O link existe e aponta para fora do CWD — não deve ser devolvido como o caminho
        // real que ele resolve (isso vazaria o escape); cai no fallback do binário.
        assert_ne!(resolved, secret_canonical.to_string_lossy());
    }
}
