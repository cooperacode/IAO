---
name: dev-initializer
description: "expandir um brief em features priorizadas e escafoldar o ambiente"
---

# SKILL: inicializador (session 0)

Você transforma um brief em um plano executável e prepara o terreno. Faça, nesta ordem:

## 0. Detectar greenfield vs brownfield
Antes de escafoldar qualquer coisa, descubra se já existe um app no diretório-alvo — o brief
pode apontar explicitamente para um caminho existente, ou o diretório-alvo padrão
(`app/<nome-descritivo>`, ver passo 2) já pode existir com conteúdo real (não vazio, não só o
que o próprio harness criou em runs anteriores).

- **Greenfield** (nada existe ainda): siga os passos 1-3 normalmente, criando tudo do zero.
- **Brownfield** (o app já existe): faça uma inspeção **limitada a metadado**, nunca ao
  código-fonte inteiro — README, manifests de build (`package.json`, `.csproj`/`.sln`,
  `Makefile`, `Dockerfile`), listagem de diretórios de topo, `docs/`/ADRs e `progress.txt` já
  existentes, se houver. O objetivo é só:
  - confirmar o `$VERIFY_CMD` **já estabelecido** (não inventar um novo se já existe);
  - entender a estrutura/convenções/arquitetura já em vigor, para as features novas se
    encaixarem nelas em vez de contradizê-las.
  Não tente entender cada funcionalidade já implementada — isso não escala num app grande e
  contradiz o princípio de contexto enxuto do harness. Investigação pontual de "já existe algo
  parecido com a feature X?" é papel de cada sessão de feature (`bearings`/`implement`), não
  desta sessão 0.

## 1. Garantir repositório Git e branch de trabalho
Se o diretório-alvo não for um repositório Git (`git rev-parse --is-inside-work-tree` falha),
rode `git init`. Em seguida, garanta uma branch dedicada a este desenvolvimento — nunca
trabalhe direto em `main`/`master`: se já estiver numa branch que não seja a padrão (ex.:
retomando um run anterior), reaproveite-a; caso contrário, crie e mude para uma nova com o
nome no formato `<YYYYMMDDHHMM>-<nome-descritivo>` — o prefixo é o timestamp UTC do momento
da criação (ex.: `202607211830`) e `<nome-descritivo>` reflete o objetivo do projeto/brief
(`git checkout -b <YYYYMMDDHHMM>-<nome-descritivo>`).

## 2. Escafoldar o ambiente
**Greenfield:** o diretório-alvo segue sempre o padrão `app/<nome-descritivo>` — só a parte
descritiva do nome da branch do passo 1, **sem** o prefixo de timestamp. Crie-o se não existir.
Dentro dele, crie:
- um `init.sh` **idempotente** que deixe o projeto pronto para rodar do zero: instalar
  dependências, restaurar/buildar e (se aplicável) subir o app. Deve poder ser rodado várias
  vezes sem quebrar;
- um `verify-feature.sh <id>` **idempotente** que verifica a feature indicada. No começo ele
  pode rodar a suite completa (`./init.sh` e depois o `$VERIFY_CMD`), sem exigir filtros por
  feature. Ele deve imprimir uma linha começando com `PASS` quando tudo passar, ou uma linha
  no formato `FAIL: <motivo>` quando falhar, e sair com código 0/não-zero de acordo com o
  resultado. Evite prosa longa no stdout; o harness captura stdout/stderr completos em
  `.harness/logs/verify-feature-<id>.log`.

Crie também a estrutura mínima de pastas do projeto.

**Brownfield:** o diretório-alvo é onde o app já vive (não necessariamente
`app/<nome-descritivo>`). **Não** recrie nem sobrescreva o que já existe: se já houver um
`init.sh` (ou pipeline equivalente — Makefile, script de bootstrap), reaproveite-o, ajustando
só o mínimo necessário para a mudança pedida. Só crie um `init.sh` do zero se realmente não
existir nada equivalente. Garanta também um `verify-feature.sh <id>` no diretório-alvo; se já
houver um wrapper de verificação equivalente, reaproveite-o ou faça um adaptador mínimo. Se
não houver convenção de filtro por feature, o wrapper deve rodar a suite completa.

## 3. Expandir em features
**Greenfield:** quebre o objetivo (o app inteiro) em features **pequenas, verticais e
verificáveis**. **Brownfield:** quebre apenas o **delta** pedido no brief — a mudança/evolução
solicitada, não o app inteiro — respeitando a arquitetura e as convenções já detectadas no
passo 0. Em ambos os casos, cada feature deve ser:
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
  Brownfield: reaproveite o comando já estabelecido no projeto quando houver um, em vez de
  propor um novo.
- `$TARGET_DIR`: `app/<nome-descritivo>` em greenfield; em brownfield, o caminho real onde o
  app já vive.

Observação: `$VERIFY_CMD` continua sendo o comando canônico do projeto. O
`verify-feature.sh <id>` é um wrapper operacional para o harness chamar sem outro turno do
modelo; inicialmente ele pode apenas executar esse comando canônico para todas as features.
O wrapper deve produzir um veredito curto (`PASS`/`FAIL`) e deixar logs detalhados no arquivo
capturado pelo harness.
