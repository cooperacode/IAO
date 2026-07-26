//! Escrita atômica de arquivos "finais" (o estado inteiro é substituído de uma vez —
//! diferente dos appends de log, que crescem linha a linha). Grava num arquivo temporário
//! no MESMO diretório do destino e troca via `rename`, que é atômico dentro da mesma
//! partição: um processo interrompido a meio da gravação (kill, queda de energia, crash)
//! nunca deixa o arquivo destino truncado/corrompido — ou fica o conteúdo antigo inteiro,
//! ou o novo inteiro.

use std::io::Write;
use std::path::Path;

/// Grava `content` em `path` de forma atômica: escreve num temp único no mesmo diretório,
/// sincroniza em disco e troca via `rename`. Limpa o temp se algo falhar antes do rename.
pub fn write_atomic(path: &Path, content: &str) -> std::io::Result<()> {
    let dir = path.parent().filter(|p| !p.as_os_str().is_empty());
    let file_name = path
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("arquivo");

    // Sem crate de uuid/rand no workspace: pid + tempo monotônico (nanos desde epoch) é
    // suficiente para um nome único o bastante — dois processos nunca colidem (pid
    // distinto), e o mesmo processo escrevendo duas vezes em sequência já avança o relógio.
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
        let path = dir.path().join("arquivo.json");

        write_atomic(&path, r#"{"a":1}"#).unwrap();

        assert_eq!(std::fs::read_to_string(&path).unwrap(), r#"{"a":1}"#);
    }

    #[test]
    fn write_atomic_sobrescreve_arquivo_existente() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("arquivo.json");
        std::fs::write(&path, "velho").unwrap();

        write_atomic(&path, "novo").unwrap();

        assert_eq!(std::fs::read_to_string(&path).unwrap(), "novo");
    }

    #[test]
    fn write_atomic_nao_deixa_arquivo_temporario_para_tras() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("arquivo.json");

        write_atomic(&path, "conteudo").unwrap();

        let sobras: Vec<_> = std::fs::read_dir(dir.path())
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| e.file_name().to_string_lossy().contains(".tmp-"))
            .collect();
        assert!(sobras.is_empty());
    }
}
