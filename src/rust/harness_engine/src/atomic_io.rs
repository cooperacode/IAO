//! Atomic writes for "final" files (the whole state is replaced at once — unlike log
//! appends, which grow line by line). Writes to a temporary file in the SAME directory as
//! the destination and swaps it in via `rename`, which is atomic within the same
//! partition: a process interrupted mid-write (kill, power loss, crash) never leaves the
//! destination file truncated/corrupted — it's either the whole old content or the whole
//! new content.

use std::io::Write;
use std::path::Path;

/// Writes `content` to `path` atomically: writes to a unique temp file in the same
/// directory, syncs it to disk, and swaps it in via `rename`. Cleans up the temp file if
/// anything fails before the rename.
pub fn write_atomic(path: &Path, content: &str) -> std::io::Result<()> {
    let dir = path.parent().filter(|p| !p.as_os_str().is_empty());
    let file_name = path
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("file");

    // No uuid/rand crate in the workspace: pid + monotonic time (nanos since epoch) is
    // unique enough — two processes never collide (distinct pid), and the same process
    // writing twice in a row already advances the clock.
    let unique = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    let tmp_name = format!("{file_name}.tmp-{}-{unique}", std::process::id());
    let tmp_path = match dir {
        Some(dir) => dir.join(tmp_name),
        None => Path::new(&tmp_name).to_path_buf(),
    };

    let result = (|| -> std::io::Result<()> {
        let mut file = std::fs::File::create(&tmp_path)?;
        file.write_all(content.as_bytes())?;
        file.sync_all()?;
        Ok(())
    })();

    match result {
        Ok(()) => std::fs::rename(&tmp_path, path),
        Err(e) => {
            let _ = std::fs::remove_file(&tmp_path);
            Err(e)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn write_atomic_grava_o_conteudo() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("file.json");

        write_atomic(&path, r#"{"a":1}"#).unwrap();

        assert_eq!(std::fs::read_to_string(&path).unwrap(), r#"{"a":1}"#);
    }

    #[test]
    fn write_atomic_sobrescreve_arquivo_existente() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("file.json");
        std::fs::write(&path, "old").unwrap();

        write_atomic(&path, "new").unwrap();

        assert_eq!(std::fs::read_to_string(&path).unwrap(), "new");
    }

    #[test]
    fn write_atomic_nao_deixa_arquivo_temporario_para_tras() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("file.json");

        write_atomic(&path, "content").unwrap();

        let leftovers: Vec<_> = std::fs::read_dir(dir.path())
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| e.file_name().to_string_lossy().contains(".tmp-"))
            .collect();
        assert!(leftovers.is_empty());
    }
}
