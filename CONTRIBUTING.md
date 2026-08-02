# Contribuindo

## Branches e pull requests

Use branches curtas para cada mudança:

```text
feat/context-usage
fix/timeout-recovery
docs/versioning
```

Abra um pull request para `main` e aguarde os checks dos quatro engines. A
branch `main` deve permanecer sempre publicável.

## Commits

Use [Conventional Commits](https://www.conventionalcommits.org/pt-br/v1.0.0/):

```text
feat: add context usage tracking
fix: recover from timeout
docs: explain the release flow
refactor: simplify feature store
test: cover invalid envelopes
chore: update dependencies
```

Para mudanças incompatíveis, use `!` ou um rodapé `BREAKING CHANGE`:

```text
feat!: change the envelope contract
```

## Versões e releases

A versão oficial fica em [`VERSION`](VERSION). Ela usa Semantic Versioning:

- `MAJOR`: mudança incompatível no protocolo ou contrato público;
- `MINOR`: funcionalidade nova compatível;
- `PATCH`: correção compatível.

Os manifests Python e Rust devem permanecer iguais à versão em `VERSION`. A
validação pode ser executada localmente com:

```bash
bash scripts/check-version.sh
```

Para publicar uma versão:

1. Atualize `VERSION`.
2. Mova as alterações de `Unreleased` para a nova seção em `CHANGELOG.md`.
3. Faça merge da mudança em `main`.
4. Crie uma tag anotada com o mesmo número:

   ```bash
   git tag -a v0.1.0 -m "Release v0.1.0"
   git push origin v0.1.0
   ```

O workflow de release valida a tag, executa os checks e publica os pacotes
Linux dos quatro engines como assets da release do GitHub.
