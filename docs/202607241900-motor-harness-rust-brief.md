# Motor do Harness em Rust — terceiro runner protocol-compatible

**Tipo:** Greenfield (novo runner dentro do monorepo existente)
**Run-id (provisório):** `202607241900`

## Status
**Ativo — promovido para a raiz de `docs/`.** Será lido pelo próximo `start` (glob
não-recursivo em `docs/`).

O run-id acima é provisório; a branch de trabalho real ganha seu próprio run-id no Step 1 do
`dev-initializer` (`git checkout -b <YYYYMMDDHHMM>-<nome-descritivo>`).

## Contexto
Este repositório implementa o padrão **Inverted Agentic Orchestration** (ver
[README.md](../../README.md)): um harness determinístico dirige agentes de IA através de um
protocolo de envelope JSON, com estado, trace e feature list persistidos em `.harness/`. Hoje
existem duas implementações protocol-compatible do motor (`Harness.Engine` /
`harness_engine`) e do flow de desenvolvimento (`Flows.Development` / `flows_development`):

- **.NET** — `src/dotnet/Harness.Engine` + `src/dotnet/Flows.Development`, invocado por
  `./run-development.sh`.
- **Python** — `src/python/harness_engine` + `src/python/flows_development`, invocado por
  `./run-development-py.sh`.

Ambas expõem o mesmo contrato: leem `.harness/inbox.json`, avançam a máquina de estados
`start -> plan -> [bearings -> smoke -> pick -> implement -> verify -> handoff]* -> stop`, e
imprimem a próxima instrução em `stdout`. Este brief propõe um **terceiro runner, em Rust**,
com o mesmo contrato — sem alterar os dois existentes.

**Meta-nota**: esta feature será construída **através do próprio harness** (dogfooding) — ou
seja, o flow `development` já existente (.NET ou Python) vai dirigir a implementação deste
port em Rust, feature a feature.

## Objetivo
Implementar um motor de harness e o flow de desenvolvimento em Rust, com **paridade
comportamental estrita** com as implementações .NET e Python: mesmos formatos de envelope,
mesmos arquivos em `.harness/`, mesmas transições de estado, mesmos guards de step/custo/timeout,
mesmos casos golden — de forma que qualquer um dos três runners possa ser trocado por outro no
meio de uma mesma execução sem quebrar o estado persistido.

## Arquitetura
Estrutura de crates espelhando a separação `engine` / `flow` já usada em .NET e Python:

```
src/rust/
├── harness_engine/     # crate lib — espelha Harness.Engine / harness_engine
└── flows_development/  # crate bin — espelha Flows.Development / flows_development
```

Módulos do `harness_engine` a portar (nomes de referência do lado .NET/Python):
`Envelope`/`EnvelopeValidation`, `HarnessHost`, `TaskRegistry`, `HarnessState`, `StateStore`,
`FeatureStore`, `ScoreStore`, `RunConfigStore`, `GoldenCaseStore`, `ArtifactStore`,
`ArtifactTemplate`, `Trace`, `Inbox`, `HarnessConfig`, `PathResolver`, `DocsReader`,
`PromptFormatter`, `Evaluators`, `BatchEvaluator`, `GitCommand`, tratamento de timeout.

Módulos do `flows_development` a portar: `DevelopmentTasks` (incluindo os sub-arquivos de
prompt, verify e handoff) e o `Program`/entrypoint equivalente.

Decisões de design já discutidas e a manter:
- Erros modelados como `Result<T, HarnessError>` (via `thiserror` ou equivalente) no lugar de
  exceções — a superfície de informação (tipo de erro, mensagem, contexto) deve continuar
  equivalente à das exceções em .NET/Python, mesmo que o mecanismo de propagação mude.
- Serialização via `serde`/`serde_json`; sem necessidade de um análogo ao
  `HarnessJsonContext` (source-gen do .NET para AOT) — `serde_derive` já resolve isso em
  tempo de compilação, e o binário Rust já é nativo por padrão.
- I/O síncrono (`std::fs`, `std::process::Command`) — o processo é one-shot por invocação, sem
  necessidade de runtime async.

## Funcionalidades desejadas (prioridade sugerida)
1. **Contrato base** — `Envelope` + `EnvelopeValidation` em Rust, com serialização idêntica
   (mesmos nomes de campo, mesma forma de erro de validação) à dos dois runners existentes.
2. **Stores de `.harness/`** — `StateStore`, `Inbox`, `Trace`, `RunConfigStore`,
   `HarnessConfig`/`PathResolver` — leitura/escrita nos mesmos arquivos e formatos já usados
   por .NET/Python, de forma que os três runners possam ler o estado uns dos outros.
3. **`HarnessHost` + `TaskRegistry`** — despacho de comandos, guards de `maxSteps`,
   `maxInstructionChars`, `timeoutMs` (lidos do `harness.json` existente), com o mesmo
   comportamento de erro corretivo descrito no README (não termina o flow silenciosamente).
4. **`DocsReader` + `PromptFormatter`** — leitura não-recursiva de `docs/*.md`/`*.txt` com o
   mesmo truncamento por `docsMaxChars` e o mesmo aviso em stderr.
5. **`FeatureStore`, `ScoreStore`, `GoldenCaseStore`, `ArtifactStore`/`ArtifactTemplate`,
   `Evaluators`/`BatchEvaluator`** — demais stores e avaliação usados pelo ciclo
   `bearings -> smoke -> pick -> implement -> verify -> handoff`.
6. **`GitCommand`** — chamadas de git usadas pelo flow (branch, commits) via
   `std::process::Command`.
7. **`flows_development`** — porta de `DevelopmentTasks` (incluindo prompt, verify e handoff)
   sobre o `harness_engine` em Rust, reproduzindo a mesma máquina de estados do flow
   `development`.
8. **`run-development-rs.sh`** — wrapper de invocação ao lado de `run-development.sh` e
   `run-development-py.sh`, buildando via `cargo build --release` sob demanda (paralelo ao
   fallback que `run-development.sh` já faz para o build .NET).
9. **Suíte de testes com paridade de casos golden** — testes em Rust cobrindo os mesmos
   cenários já exercitados por `Harness.Engine.Tests` (.NET) e pelos testes em
   `src/python/tests`, validando que os três runners produzem a mesma sequência de
   instruções para a mesma entrada.

## Regras / restrições
- **Paridade comportamental estrita**: para uma mesma sequência de envelopes de entrada, o
  runner Rust deve produzir a mesma sequência de instruções em `stdout`, os mesmos arquivos em
  `.harness/` (mesma estrutura/campos) e os mesmos casos golden que já passam em .NET e Python
  — não é uma reimplementação "idiomática" livre de contrato.
- **Sem alterar os runners .NET e Python existentes** — o port é aditivo; nenhuma mudança de
  protocolo é introduzida por esta feature.
- **`harness.json` é a mesma fonte de configuração** — nenhum campo novo, nenhum formato
  paralelo.
- **`Cargo build --release` deve produzir um binário nativo** utilizável por
  `run-development-rs.sh`, sem exigir toolchain adicional além de Rust/Cargo instalados.

## Fora de escopo (por enquanto)
- Portar outros flows além de `development` (o repositório hoje só tem esse flow).
- Mudanças no protocolo de envelope ou no formato de `.harness/` — este brief não é sobre
  evoluir o contrato, só sobre um terceiro runner compatível com o que já existe.
- Empacotamento em `package.sh` para os três alvos (.NET AOT, Python, Rust) — pode ser um
  delta futuro depois que o port estiver com paridade comprovada.
- Benchmark formal comparando custo/performance entre os três runners (like o experimento
  `GF-V2` do README) — pode ser um brief separado depois que o port existir.

## Critério de "pronto"
`./run-development-rs.sh '{ "type": "text", "value": "start" }'` — e a sequência completa do
ciclo `start -> plan -> bearings -> smoke -> pick -> implement -> verify -> handoff -> stop`
contra um brief de exemplo em `docs/` — produzem instruções, arquivos em `.harness/` e trace
equivalentes aos runners .NET e Python para a mesma entrada. A suíte de testes Rust cobre os
mesmos casos golden já usados em `Harness.Engine.Tests`/`src/python/tests`, e passa integralmente
(verde), sem regressão nos runners .NET e Python existentes.
