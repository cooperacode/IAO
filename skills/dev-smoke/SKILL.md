---
name: dev-smoke
description: "smoke test do baseline antes de implementar"
---

# SKILL: smoke test

Antes de mexer em qualquer feature, confirme que o baseline está saudável — assim, se algo
quebrar depois, você sabe que foi a sua mudança e não um estado herdado quebrado.

- Rode `./init.sh` no diretório-alvo com saída completa redirecionada para
  `.harness/logs/smoke.log` (crie a pasta se necessário).
- Verifique que o app sobe/builda sem erro (o mínimo que deveria funcionar, funciona).
- Se falhar, leia só o trecho relevante do log (ex.: primeira falha, `tail -n 80`, arquivo
  citado pela stack trace). Não cole o log inteiro no contexto.

Se o smoke **falhar**, o baseline está quebrado: conserte isso primeiro — não empilhe uma
feature nova sobre um chão instável. Relate o resultado em `$SMOKE` como `ok` ou
`FAIL: <erro principal>. Log: <caminho>`.
