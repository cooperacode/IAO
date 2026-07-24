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

Não inclua logs completos no commit. Logs operacionais do harness devem ficar em
`.harness/logs/`, que é estado local ignorado pelo Git; cite o caminho no `progress.txt`
quando ele explicar como investigar uma falha/validação.

## 2. Atualizar o progresso
Anexe **uma única linha física** ao `progress.txt` com: a feature concluída (id + título), o
que foi feito, e como verificar. É o que a próxima sessão lê no `bearings` para se orientar —
um `tail -n 20` só funciona como resumo rápido se cada entrada couber numa linha.

Antes de escrever, rode de fato `date -u +"%Y-%m-%d %H:%M UTC"` no shell e use a saída literal
como prefixo entre colchetes — nunca escreva `UTC` sem hora (isso quebra o parsing do
timestamp e o propósito do prefixo como separador entre sessões):
`[2026-07-21 20:18 UTC] Feature #8 - Filtrar listagem...: <o que foi feito>. Verificar com: ...`

Nunca quebre a entrada em parágrafos/blocos multi-linha, por mais detalhado que seja o que foi
feito — resuma. Se sentir necessidade de mais detalhe do que cabe numa linha, esse detalhe
pertence a `.harness/logs/` (citado por caminho), não ao `progress.txt`.

Antes de anexar, confira as últimas linhas do `progress.txt`: se o handoff automático do
harness já registrou uma entrada `[data HH:MM UTC] Feature #<id> - ...` para esta mesma
feature, não crie uma segunda — isso duplica o registro em dois formatos diferentes para o
mesmo trabalho. Este passo manual é para preencher a lacuna quando o handoff automático falhou
ou não rodou, não para complementar uma entrada que já existe.

Escreva de forma que alguém sem o seu contexto entenda em 10 segundos onde o trabalho está.

Confirme com o hash do commit em `$COMMIT`.
