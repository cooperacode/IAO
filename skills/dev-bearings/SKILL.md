---
name: dev-bearings
description: "orientar-se no início de uma sessão de contexto fresco"
---

# SKILL: get bearings

Você começa uma sessão **fresca**, sem memória das anteriores. Antes de tocar em código,
reconstrua o contexto a partir dos artefatos persistentes — só eles são confiáveis:

- `pwd` e liste o diretório-alvo para saber onde está.
- Leia o `progress.txt`: cada linha traz um timestamp UTC entre colchetes — é o separador leve
  entre sessões. Use-o para identificar rapidamente a entrada mais recente (o fim do trabalho
  já feito) sem precisar de cabeçalhos ou blocos multi-linha.
- Rode `git log --oneline -15`: o histórico recente confirma o que os commits registraram.

Se `progress.txt` ainda não existir (primeira feature do harness rodando neste diretório —
comum em app brownfield que nunca foi tocado pelo harness antes), crie-o agora com uma linha
inicial de contexto, e não confie no `git log -15` geral do projeto para se orientar: num
repositório grande e preexistente, os commits recentes podem ser de outra equipe/período e não
dizer nada sobre a mudança em curso. Nesse caso, oriente-se pelo brief da feature atual e pelo
estado real do código relevante a ela, não pelo histórico do projeto inteiro.

Não confie em suposições sobre o estado — verifique. Resuma em `$NOTE`, em 2–4 linhas, o que
você encontrou e onde o trabalho parou.
