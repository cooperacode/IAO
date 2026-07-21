---
name: dev-verify
description: "self-verify da feature como um usuário faria"
---

# SKILL: E2E self-verify

Verifique a feature **como um usuário faria**, não só que o código compila. O objetivo é
provar o comportamento ponta a ponta antes de dar a feature por concluída.

- Rode o comando de verificação do projeto (`$VERIFY_CMD`) no diretório-alvo e observe o
  resultado real — não presuma que passou.
- Quando fizer sentido, exercite o caminho do usuário de verdade (a rota, a tela, a chamada),
  não apenas o teste unitário.
- Seja honesto: um falso "passou" só empurra o problema para a próxima sessão, que começa sem
  o seu contexto e terá mais dificuldade de achar a causa.

Responda em `$RESULT` começando com:
- `PASS` — tudo verde, comportamento confirmado; ou
- `FAIL: <motivo curto>` — o que falhou (será o gancho para a correção).
