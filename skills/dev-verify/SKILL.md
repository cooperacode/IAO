---
name: dev-verify
description: "self-verify da feature como um usuário faria"
---

# SKILL: E2E self-verify

Verifique a feature **como um usuário faria**, não só que o código compila. O objetivo é
provar o comportamento ponta a ponta antes de dar a feature por concluída.

- Se houver `verify-feature.sh` no diretório-alvo, rode `./verify-feature.sh <id-da-feature>`;
  ele é o wrapper preferencial do harness e pode executar a suite completa. Quando rodar
  manualmente, redirecione a saída completa para `.harness/logs/verify-<id>.log`.
- Se não houver wrapper, rode o comando de verificação do projeto (`$VERIFY_CMD`) no
  diretório-alvo e observe o resultado real — não presuma que passou. Também redirecione a
  saída completa para `.harness/logs/verify-<id>.log`.
- Leia no contexto só o trecho necessário do log (`tail -n 80`, primeira falha, stack trace
  relevante). Não cole logs inteiros.
- Quando fizer sentido, exercite o caminho do usuário de verdade (a rota, a tela, a chamada),
  não apenas o teste unitário.
- Seja honesto: um falso "passou" só empurra o problema para a próxima sessão, que começa sem
  o seu contexto e terá mais dificuldade de achar a causa.

Responda em `$RESULT` começando com:
- `PASS: <resumo curto>. Log: <caminho>` — tudo verde, comportamento confirmado; ou
- `FAIL: <motivo curto>. Log: <caminho>` — o que falhou (será o gancho para a correção).
