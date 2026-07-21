---
name: dev-initializer
description: "expandir um brief em features priorizadas e escafoldar o ambiente"
---

# SKILL: inicializador (session 0)

Você transforma um brief em um plano executável e prepara o terreno. Faça, nesta ordem:

## 1. Garantir repositório Git e branch de trabalho
Se o diretório-alvo não for um repositório Git (`git rev-parse --is-inside-work-tree` falha),
rode `git init`. Em seguida, garanta uma branch dedicada a este desenvolvimento — nunca
trabalhe direto em `main`/`master`: se já estiver numa branch que não seja a padrão (ex.:
retomando um run anterior), reaproveite-a; caso contrário, crie e mude para uma nova
(`git checkout -b <nome-descritivo>`, refletindo o objetivo do projeto/brief).

## 2. Escafoldar o ambiente
O diretório-alvo segue sempre o padrão `app/<nome-descritivo>` (o mesmo `<nome-descritivo>`
usado na branch do passo 1). Crie-o se não existir. Dentro dele, crie um `init.sh`
**idempotente** que deixe o projeto pronto para rodar do zero: instalar dependências,
restaurar/buildar e (se aplicável) subir o app. Deve poder ser rodado várias vezes sem quebrar.
Crie também a estrutura mínima de pastas do projeto.

## 3. Expandir em features
Quebre o objetivo em features **pequenas, verticais e verificáveis** — cada uma:
- implementável isoladamente em uma sessão curta;
- testável de forma independente (tem como dizer "passou" sem ambiguidade);
- com **prioridade** numérica (1 = mais alta); se uma feature depende de outra (precisa de algo
  que a outra cria), registre isso em `dependsOn` — o harness só a libera depois que a(s)
  dependência(s) passar(em), além de respeitar a prioridade.

Prefira muitas features pequenas a poucas grandes. Uma feature que não dá para verificar
sozinha está grande demais — quebre.

## Saída
- `$FEATURES`: um ARRAY JSON `[{"id":1,"title":"...","priority":1,"dependsOn":[]}, ...]` (só o
  array; não inclua `passes` — toda feature nasce pendente; `dependsOn` vazio quando não houver
  dependência).
- `$VERIFY_CMD`: o comando único que verifica o projeto (ex.: `dotnet test`, `npm test`).
- `$TARGET_DIR`: sempre `app/<nome-descritivo>` — o diretório onde o código vive.
