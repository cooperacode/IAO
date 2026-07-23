---
name: dev-bearings
description: "orientar-se no início de uma sessão de contexto fresco"
---

# SKILL: get bearings

Você começa uma sessão **fresca**, sem memória das anteriores. Antes de tocar em código,
reconstrua o contexto a partir dos artefatos persistentes — só eles são confiáveis:

- `pwd` e liste só o topo do diretório-alvo para saber onde está.
- Leia só o fim do `progress.txt` (ex.: `tail -n 20 progress.txt`): cada linha traz um
  timestamp UTC entre colchetes — é o separador leve entre sessões. Use-o para identificar
  rapidamente a entrada mais recente sem despejar o histórico inteiro no contexto.
- Rode `git log --oneline -10`: o histórico recente confirma o que os commits registraram.
- Não abra logs completos por padrão. Se precisar investigar um log em `.harness/logs/`, leia
  primeiro só um trecho pequeno (`tail -n 80`, busca por erro, arquivo específico).

Se `progress.txt` ainda não existir (primeira feature do harness rodando neste diretório —
comum em app brownfield que nunca foi tocado pelo harness antes), crie-o agora com uma linha
inicial de contexto, e não confie no `git log -15` geral do projeto para se orientar: num
repositório grande e preexistente, os commits recentes podem ser de outra equipe/período e não
dizer nada sobre a mudança em curso. Nesse caso, oriente-se pelo brief da feature atual e pelo
estado real do código relevante a ela, não pelo histórico do projeto inteiro.

Não confie em suposições sobre o estado — verifique. Resuma em `$NOTE`, em 2–4 linhas, o que
você encontrou e onde o trabalho parou. Não cole logs, diffs ou listagens longas no `$NOTE`.
