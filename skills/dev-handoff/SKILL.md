---
name: dev-handoff
description: "deixar estado limpo para a próxima sessão"
---

# SKILL: leave clean state

A próxima sessão começa **sem o seu contexto** — ela só terá o que você deixar registrado.
Um handoff limpo é o que torna o loop retomável.

## 1. Commit descritivo
`git commit` com uma mensagem que explique **o quê** e **por quê**, referenciando a feature.
Um working tree limpo é a garantia de que o próximo `git log`/bisect faz sentido. Não deixe
mudanças não commitadas nem arquivos temporários.

## 2. Atualizar o progresso
Anexe uma linha ao `progress.txt` com: a feature concluída (id + título), o que foi
feito, e como verificar. É o que a próxima sessão lê no `bearings` para se orientar.

Prefixe a linha com um timestamp UTC entre colchetes (`date -u +"%Y-%m-%d %H:%M UTC"`) — é o
separador leve entre sessões, sem precisar de cabeçalhos ou blocos multi-linha:
`[2026-07-21 20:18 UTC] Feature #8 - Filtrar listagem...: <o que foi feito>. Verificar com: ...`

Escreva de forma que alguém sem o seu contexto entenda em 10 segundos onde o trabalho está.

Confirme com o hash do commit em `$COMMIT`.
