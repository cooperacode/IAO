---
name: dev-smoke
description: "smoke test do baseline antes de implementar"
---

# SKILL: smoke test

Antes de mexer em qualquer feature, confirme que o baseline está saudável — assim, se algo
quebrar depois, você sabe que foi a sua mudança e não um estado herdado quebrado.

- Rode `./init.sh` no diretório-alvo.
- Verifique que o app sobe/builda sem erro (o mínimo que deveria funcionar, funciona).

Se o smoke **falhar**, o baseline está quebrado: conserte isso primeiro — não empilhe uma
feature nova sobre um chão instável. Relate o resultado em `$SMOKE` (ok, ou o erro exato).
