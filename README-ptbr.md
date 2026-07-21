# Inverted Agentic Orchestration - máquina de estados em código, dirigida por prompt

Harness em que a **orquestração vive em .NET compilado** e o agente da IDE age
como **interpretador**: a cada passo ele roda um comando, lê o `stdout` e
responde no contrato esperado. Nenhum SDK de modelo é necessário, apenas um
terminal, o que torna o mesmo flow portável entre Claude Code, GitHub Copilot,
Devin e Codex.

Neste branch, o flow executável é o **development**: um fluxo long-running que
transforma um brief em uma lista priorizada de features e implementa uma feature
por sessão até todas passarem.

## O que existe neste branch

- Engine reutilizável em `src/Harness.Engine`.
- Flow de desenvolvimento em `src/Flows.Development`.
- Wrapper estável `run-development.sh`, que executa o binário Native AOT quando
  existir ou cai para a DLL via `dotnet`.
- Adaptadores de IDE para Claude Code, GitHub Copilot, Devin e Codex.
- `package.sh`, que gera um pacote autocontido com binário Native AOT, skills e
  o adaptador da IDE escolhida.
- Primitivos determinísticos de avaliação na biblioteca (`Evaluators.cs`,
  `BatchEvaluator.cs`, `GoldenCaseStore.cs`, `ScoreStore.cs`), mas sem
  CLI/flow de avaliação empacotado neste branch.

Os antigos flows `refinement`/`evaluation`, scripts `run-refinement.sh` /
`run-eval.sh` e CLI `src/Bench.Eval` não fazem parte da estrutura atual desta
branch.

## Por que usar essa técnica

- A lógica de fluxo, validação e estado fica em **código**, não na janela de
  contexto: o fluxo é determinístico, testável e versionável.
- **Portabilidade**: o mesmo `run-*.sh` pode ser dirigido por diferentes agentes
  de IDE sem SDK específico.
- O teto de passos evita loop infinito e torna o gasto previsível.
- O estado do run em andamento é persistido em `.harness/state.json`, então
  cada invocação do binário (uma por passo) não precisa carregar o histórico
  na janela de contexto do agente. É estado vivo *daquele run* — sobrevive
  entre invocações do processo, mas não é o que decide se um novo `start`
  reseta ou retoma (veja "Retomando entre sessões" abaixo, isso é decidido
  por `.harness/feature_list.json`).
- **`start` é seguro de enviar sempre**, mesmo trocando de agente/driver no
  meio de uma feature: o próprio `DevelopmentTasks.Start()` decide em código
  se reseta (nenhum run pendente) ou retoma (há feature pendente) — não
  depende do agente saber disso.
- Native AOT permite distribuir um binário nativo autocontido; quem só roda o
  pacote não precisa ter o runtime .NET instalado.

Isto não é uma técnica para economizar tokens por si só. Um flow com N passos
gera N interações com o modelo. O ganho aqui é controle operacional:
trajetória governada, validação por passo, continuidade auditável e
portabilidade.

## Quando usar

Use o harness quando pelo menos um destes pontos for importante:

- A tarefa precisa seguir uma ordem fixa de passos, validada pelo código.
- O mesmo fluxo precisa rodar em mais de uma IDE/agente.
- É necessário impor teto de passos ou de tamanho de instrução.
- O estado do processo precisa ser auditável (trace + snapshots) e a
  continuidade entre sessões frescas precisa vir de artefatos persistentes no
  disco, não da memória do agente.
- O trabalho é naturalmente incremental, como implementar uma lista de features
  pequenas e verificáveis.

Prefira um prompt único quando a tarefa couber em uma resposta direta, não houver
necessidade de auditoria/continuidade entre sessões e custo de tokens for a
prioridade principal.

## Flow de desenvolvimento

O flow atual segue esta trajetória:

```text
start -> plan -> [bearings -> smoke -> pick -> implement -> verify -> handoff]*
```

Como ele funciona:

- `start` primeiro checa se já existe uma feature pendente em
  `.harness/feature_list.json`. **Se houver**, não reseta nada — retoma
  direto no passo `bearings` da feature em andamento (veja "Retomando entre
  sessões" abaixo). **Se não houver** (primeiro `start` do run, ou o run
  anterior já terminou com tudo passando), aí sim começa do zero: zera
  `.harness/state.json` e `.harness/trace.jsonl` (camada genérica da engine)
  e apaga `.harness/feature_list.json` e `.harness/run_config.json` (camada
  do flow `development`), depois lê `docs/*.md` / `docs/*.txt` como brief. Se
  `docs/` estiver vazio, pede objetivo, diretório-alvo e comando de
  verificação ao usuário.
- `plan` grava `.harness/feature_list.json` e `.harness/run_config.json`
  (comando de verificação e diretório-alvo).
- `bearings` inicia uma nova sessão de feature e orienta o agente a ler estado
  persistente (`progress.txt`, `git log`, `pwd`).
- `smoke` exige rodar `./init.sh` no diretório-alvo antes de qualquer mudança.
- `pick` escolhe deterministicamente a feature de maior prioridade entre as PRONTAS
  (todas as `dependsOn` já concluídas).
- `implement` limita o trabalho à feature escolhida.
- `verify` espera uma resposta com `PASS` ou `FAIL: <motivo>`; em caso de falha,
  volta para `implement` na mesma feature.
- `handoff` exige commit ou `NO_GIT: <motivo>`, atualiza a feature como concluída
  e segue para a próxima. Quando todas passam, retorna `stop`.

O flow publica snapshots em `.harness/last-development.trace.jsonl` e
`.harness/last-development.state.json`. O estado vivo fica em `.harness/state.json`,
`.harness/trace.jsonl`, `.harness/feature_list.json` e `.harness/run_config.json`.

### Retomando entre sessões

Não existe comando `resume` neste branch — e não precisa: **`start` já
decide sozinho, em código, se reseta ou retoma.** A regra vive em
`DevelopmentTasks.Start()` (`src/Flows.Development/DevelopmentTasks.cs`) e é
puramente mecânica:

- `.harness/feature_list.json` tem alguma feature com `passes: false`? Então
  há um run em andamento — `start` **não reseta nada**, só devolve a mesma
  instrução que `bearings` devolveria. Isso cobre exatamente o caso de trocar
  de agente/driver no meio de uma feature (ex.: os tokens acabaram no Claude,
  o Codex assume): o novo processo pode mandar `start` como primeiro comando,
  sempre, sem precisar saber que já existia um run.
- Caso contrário (nenhuma feature list, ou todas já com `passes: true`), não
  há nada para retomar — `start` reseta `.harness/feature_list.json` e
  `.harness/run_config.json` (`.harness/state.json`/`.harness/trace.jsonl` já
  resetam incondicionalmente em todo `start`, ver abaixo), e começa um run
  genuinamente novo.

Isso funciona porque `verify_cmd`/`target_dir` (gravados por `plan` em
`.harness/run_config.json`, via `RunConfigStore`) e `feature_list.json` vivem
**fora** de `state.json` — não são afetados pelo reset genérico que
`TaskRegistry.Dispatch` sempre faz em `state.json`/`trace.jsonl` a cada
`start`. `current_feature_id`, `current_feature_title` e o teto de passos por
feature são regravados do zero pelos passos seguintes (`pick`, `bearings`)
assim que o loop recomeça, então não precisam sobreviver ao reset.

**Limite conhecido:** a retomada é por *fronteira de feature*, não por
posição exata dentro dela. Se a sessão morreu no meio de `implement` ou
esperando o veredito de `verify`, retomar reinicia a sessão daquela feature
do zero (`bearings → smoke → pick → implement → verify`) — não recupera o
`stdout` exato que estava pendente (esse texto nunca é persistido em disco).
Trabalho já commitado não se perde; trabalho não commitado deve ser
recuperado pelo agente olhando `git log`/estado do worktree durante o
`bearings`, não repetido às cegas.

A reorientação de conteúdo (o quê já foi feito, decisões tomadas) continua
sendo 100% via artefatos em disco, nunca a janela de contexto do agente:
`progress.txt` no diretório-alvo (o diário que o próprio agente mantém) e
`git log`/estado do worktree. É o que o passo `bearings` e a skill
`dev-bearings` instruem o agente a fazer a cada nova sessão de feature.

## Estrutura

```text
src/
  Harness.Engine/             # engine reutilizável, agnóstica de domínio
    Envelope.cs               # contrato JSON {type,value,args,context}
    TaskRegistry.cs           # dispatch, validação, teto de passos e timeout
    PromptFormatter.cs        # monta <input>/<response>/<skills>
    StateStore.cs             # estado persistente em .harness/state.json
    Trace.cs                  # trace.jsonl e snapshots
    FeatureStore.cs           # feature_list.json do flow development
    RunConfigStore.cs         # run_config.json (verify_cmd/target_dir), fora de state.json de propósito
    Evaluators.cs             # métricas determinísticas reutilizáveis
    BatchEvaluator.cs         # avaliação offline como biblioteca
    GoldenCaseStore.cs        # carrega casos de golden set
    ArtifactStore.cs          # store reutilizável para artefatos de outros flows
    ScoreStore.cs             # store reutilizável para notas de outros flows
    HarnessJsonContext.cs     # source generation JSON para Native AOT
    DocsReader.cs, Inbox.cs, HarnessConfig.cs, PathResolver.cs
  Flows.Development/
    Program.cs                # registra as tasks e chama HarnessHost.Run
    DevelopmentTasks.cs       # máquina de estados do flow development
    DevelopmentTasks.Prompt.cs # prompts emitidos por cada estado
  Harness.Engine.Tests/       # testes da engine e do flow development
skills/
  dev-*/SKILL.md              # skills injetadas sob demanda
run-development.sh            # wrapper estável para Flows.Development
run-checks.sh                 # testes .NET + smoke determinístico do flow
package.sh                    # empacota binário, skills e adaptador de IDE
harness.json                  # configuração global do harness
```

## Build e verificação

Pré-requisito para compilar: .NET SDK compatível com o `TargetFramework` dos
projetos (`net10.0`).

Build DLL, útil para desenvolvimento local:

```bash
dotnet build src/Flows.Development/Flows.Development.csproj -c Release
```

Native AOT, recomendado para pacote autocontido:

```bash
dotnet publish src/Flows.Development/Flows.Development.csproj -c Release -r osx-arm64
```

RIDs usados pelo empacotamento: `osx-arm64`, `osx-x64`, `linux-x64`,
`linux-arm64`, `win-x64`.

O wrapper procura primeiro um binário nativo publicado em
`src/Flows.Development/bin/Release/net10.0/<RID>/publish/`. Se não encontrar,
usa `src/Flows.Development/bin/Release/net10.0/Flows.Development.dll` via
`dotnet`.

```bash
./run-development.sh '{ "type": "text", "value": "start" }'
```

Para validar a camada determinística localmente:

```bash
./run-checks.sh
```

Esse script roda `dotnet test src/Harness.Engine.Tests/Harness.Engine.Tests.csproj
-c Release` e um smoke test ponta a ponta em um diretório temporário, usando o
transporte por inbox. Ele não precisa de agente nem consome tokens.

Para gerar um pacote completo para uma IDE:

```bash
./package.sh --os osx-arm64 --ide codex --version 1.0.0
```

IDEs suportadas: `claude`, `copilot`, `devin`, `codex`.

## Contrato do canal

- `stdout` é a **próxima instrução**: `stop` ou um bloco
  `<input>`/`<response>`.
- `stderr` é **diagnóstico** e não deve ser usado para decidir o próximo passo.
- A resposta do agente deve ser apenas o JSON pedido em `<response>`, em uma
  linha, sem cercas Markdown.

Exemplo manual por argumento, útil para entender o protocolo:

```bash
./run-development.sh '{ "type": "text", "value": "start" }'
./run-development.sh '{ "type": "command", "value": "plan", "args": ["[{\"id\":1,\"title\":\"Login\",\"priority\":1}]", "dotnet test", "app"] }'
./run-development.sh '{ "type": "command", "value": "pick" }'
```

JSON malformado ou comando inexistente retornam `ERRO no protocolo` em `stdout`
e detalhes em `stderr`, sem encerrar automaticamente o flow.

## Transporte por inbox

O harness aceita dois transportes, nesta ordem:

1. Se houver argumento de linha de comando, usa `args[0]`.
2. Se rodar sem argumentos, lê `.harness/inbox.json`.

Os adaptadores de IDE usam sempre a inbox: o agente escreve o envelope em
`.harness/inbox.json` e roda `./run-development.sh` sem argumentos. Isso evita a
falha estrutural de passar JSON single-quoted no shell: uma aspa esquecida trava o
shell antes de o binário rodar, impedindo a engine de validar o erro.

Depois de um parse bem-sucedido, a inbox é movida para
`.harness/inbox.consumed.json` para deixar rastro de debug e evitar
reprocessamento acidental.

```bash
mkdir -p .harness
printf '%s' '{ "type": "text", "value": "start" }' > .harness/inbox.json
./run-development.sh
```

## Rodando por IDE

O flow de desenvolvimento tem um adaptador por IDE:

| IDE | Adaptador |
|---|---|
| Claude Code | `.claude/agents/development.agent.md` |
| GitHub Copilot | `.github/prompts/development.prompt.md` |
| Devin | `.devin/workflows/development.md` |
| Codex | `.codex/agents/development.toml` |

Fluxo esperado de uso:

1. Rode o build DLL ou publique Native AOT.
2. Coloque o brief em `docs/` ou informe o objetivo no modo interativo.
3. Peça ao agente da IDE para usar o flow `development`.
4. O agente dirige `./run-development.sh` até receber `stop`.

## Criando um novo flow

1. Crie `src/Flows.<Nome>/` e referencie `src/Harness.Engine`.
2. Implemente a máquina de estados e os prompts do domínio.
3. No `Program.cs`, registre `Dictionary<string, Func<Envelope?, string>>` e chame
   `HarnessHost.Run(args, tasks, ...)`.
4. Crie um wrapper `run-<nome>.sh` apontando para o binário/DLL do novo flow.
5. Se o flow tiver adaptadores de IDE ou empacotamento, atualize `package.sh`.

Nenhuma lógica de orquestração precisa ser reescrita: dispatch, validação,
estado, trace, timeout, inbox e snapshots ficam em `Harness.Engine`.

## Notas de implementação

- `Envelope`, `HarnessState`, `TraceEntry`, `RunConfig`, `ScoreReport` e `GoldenCase` usam
  source generation via `HarnessJsonContext`, não reflexão, para manter
  compatibilidade com Native AOT.
- `Envelope` é um `record` com `Equals`/`GetHashCode` customizados: records
  comparam arrays por referência, o que quebraria a semântica de valor esperada
  de `Args`.
- `Envelope` aceita `context` opcional, por exemplo
  `{"driver":"codex"}`. A engine persiste esse contexto no state e o
  `PromptFormatter` o reinjeta automaticamente nas saídas seguintes.
- Cada flow deve publicar snapshots no próprio caminho. O `development` usa
  `.harness/last-development.*`; os defaults reutilizáveis ainda existem como
  `.harness/last-run.*`.
- Os arquivos de avaliação e artefatos em `Harness.Engine` são componentes de
  biblioteca. Eles permanecem úteis para novos flows, mas não significam que este
  branch tenha um executável `evaluation` pronto.

## Citação

Justino, Y. (2026). Inverted Orchestration in Software Development: A Deterministic Harness and Looping Engineering under Enterprise Constraints (Version v0.1.0). Zenodo. https://doi.org/10.5281/zenodo.21421908

```tex
@misc{justino_2026_21421908,
  author       = {Justino, Yan},
  title        = {Inverted Orchestration in Software Development: A
                   Deterministic Harness and Looping Engineering
                   under Enterprise Constraints
                  },
  month        = jul,
  year         = 2026,
  publisher    = {Zenodo},
  version      = {v0.1.0},
  doi          = {10.5281/zenodo.21421908},
  url          = {https://doi.org/10.5281/zenodo.21421908},
}
```