# Refinamento — flow de artefatos das fases iniciais do SDLC

**Status:** Proposto  
**Data:** 2026-07-26  
**Implementação-alvo:** `src/dotnet/Flows.Refinement/`  
**Runner proposto:** `./run-refinement.sh`

## 1. Resumo

Criar um novo flow .NET, chamado `Flows.Refinement`, que transforme uma ideia,
um brief ou documentação existente em um conjunto coerente e rastreável de
artefatos das três fases iniciais do ciclo de vida de desenvolvimento de
software:

1. Planejamento;
2. Análise;
3. Projeto/Design.

O flow deve conduzir um agente por uma máquina de estados determinística,
persistir cada artefato, revisar a consistência entre eles e publicar a versão
aprovada em `docs/`, pronta para orientar um flow posterior de desenvolvimento.

Não fazem parte deste flow a escrita de código, a execução de testes, a
implantação do software ou sua manutenção. Critérios de aceite, atributos de
qualidade e riscos de implantação podem ser documentados como requisitos ou
restrições de design, mas o flow não deve executar essas fases.

## 2. Contexto e problema

O repositório já possui o flow `Flows.Development`, que lê briefs em `docs/`,
gera uma lista de features e conduz sua implementação incremental. Falta,
entretanto, um flow anterior que estruture a ideia antes que ela chegue à
programação.

Sem esse passo, um brief pode chegar ao desenvolvimento com problemas como:

- objetivos, stakeholders e limites de escopo pouco claros;
- requisitos funcionais e não funcionais incompletos ou não mensuráveis;
- riscos, restrições, segurança e conformidade tratados tardiamente;
- decisões arquiteturais sem justificativa;
- ausência de rastreabilidade entre objetivo, requisito e componente;
- ambiguidades que geram retrabalho durante a programação.

O novo flow deve preencher essa lacuna sem incorporar atividades que pertencem
ao `Flows.Development` ou às fases posteriores do SDLC.

## 3. Fundamentação nas referências

As referências adotam sete fases equivalentes, apesar da diferença de
terminologia entre “programação”, “desenvolvimento”, “implementação” e
“implantação”.

| Fase coberta | Síntese da IBM | Síntese da Microsoft | Aplicação no novo flow |
|---|---|---|---|
| Planejamento | Define metas, escopo, problema, usuários, interações, recursos, riscos e cronograma; produz um plano inicial e uma especificação inicial de requisitos. | Define metas, necessidades dos stakeholders, viabilidade, recursos, tempo e orçamento. | Produzir o plano de projeto, com objetivos, escopo, viabilidade, stakeholders, riscos e restrições. |
| Análise | Detalha requisitos, viabilidade, recursos, conformidade e segurança até formar um plano prático. | Analisa como a solução deve funcionar e recomenda especificações, casos de uso e fluxos de dados. | Produzir uma especificação detalhada e rastreável de requisitos de software. |
| Projeto/Design | Define arquitetura, interfaces, dados, integrações, dependências, modularidade, protótipos e modelagem de ameaças; produz o SDD. | Define arquitetura, interação entre componentes, modelos, padrões e protótipos. | Produzir o documento de design de software e as decisões arquiteturais. |

As duas fontes também tratam segurança como preocupação transversal. Portanto,
o flow deve considerar objetivos de segurança no planejamento, riscos e
requisitos de segurança na análise e controles arquiteturais no design.

Referências:

- [IBM — O que é o ciclo de vida de desenvolvimento de software (SDLC)?](https://www.ibm.com/br-pt/think/topics/sdlc)
- [Microsoft — Ciclo de Vida do Desenvolvimento de Software](https://www.microsoft.com/pt-br/power-platform/topics/phases-of-the-software-development-lifecycle)

## 4. Objetivos

### 4.1 Objetivo principal

Transformar insumos ainda incompletos em documentação suficiente, consistente e
rastreável para que uma equipe possa iniciar o desenvolvimento com menos
ambiguidades e riscos.

### 4.2 Objetivos específicos

- padronizar os artefatos mínimos das fases de Planejamento, Análise e Design;
- solicitar esclarecimentos quando faltarem decisões que alterem materialmente
  o produto;
- registrar premissas sem apresentá-las como fatos confirmados;
- atribuir identificadores estáveis a objetivos, requisitos, riscos e decisões;
- manter rastreabilidade de ponta a ponta entre os artefatos;
- incorporar segurança, privacidade e conformidade desde o início;
- revisar a completude e a coerência antes da publicação;
- persistir estado e artefatos para permitir retomada por outro driver;
- entregar documentação diretamente consumível pelo `Flows.Development`;
- produzir evidências de execução em estado e trace.

## 5. Escopo

### 5.1 Incluído

- leitura de briefs, notas e documentos `.md` ou `.txt`;
- coleta interativa de contexto quando não houver documentação suficiente;
- identificação de problema, oportunidade, usuários e stakeholders;
- definição de objetivos, métricas de sucesso, escopo e fora de escopo;
- análise preliminar de viabilidade, recursos, dependências, restrições e riscos;
- especificação de requisitos funcionais e não funcionais;
- regras de negócio, atores, casos de uso, jornadas e fluxos de dados;
- critérios de aceite que tornem os requisitos verificáveis;
- definição de arquitetura, componentes, integrações, interfaces e dados;
- registro de decisões e trade-offs arquiteturais;
- modelagem preliminar de ameaças e controles de segurança;
- diagramas textuais, preferencialmente Mermaid;
- protótipos conceituais ou wireframes textuais, quando úteis;
- matriz de rastreabilidade e parecer de prontidão;
- publicação dos artefatos finais em `docs/`.

### 5.2 Fora de escopo

- criação ou alteração de código-fonte da solução;
- scaffold de aplicação, banco, infraestrutura ou projeto de testes;
- compilação, build ou execução da aplicação;
- criação de testes automatizados, scripts de teste ou execução de testes;
- correção de bugs;
- implantação, configuração ou alteração de ambientes;
- execução de migrações, provisionamento ou publicação de releases;
- operação, suporte e manutenção pós-implantação;
- compromisso definitivo de prazo ou orçamento sem dados fornecidos;
- aprovação humana presumida: decisões não confirmadas devem permanecer
  explicitamente abertas.

### 5.3 Fronteira com a fase de Testes

São permitidos critérios de aceite e requisitos mensuráveis porque eles fazem
parte da qualidade da especificação. Também é permitido registrar, em alto
nível, quais características precisarão ser verificadas no futuro.

Não são permitidos neste flow:

- planos detalhados de execução de testes;
- implementação de casos ou automações de teste;
- criação de massa de teste;
- execução ou registro de resultados de teste;
- emissão de um veredito de qualidade sobre software já implementado.

## 6. Atores e responsabilidades

| Ator | Responsabilidade |
|---|---|
| Solicitante/stakeholder | Fornecer contexto, responder dúvidas materiais e validar decisões de negócio quando necessário. |
| Driver/agent | Interpretar as instruções, analisar os insumos, fazer perguntas e devolver os artefatos no contrato exigido. |
| `Flows.Refinement` | Determinar a próxima etapa, validar respostas, controlar revisões, persistir artefatos e publicar o resultado. |
| `Harness.Engine` | Executar dispatch, guardas, transporte, estado, trace, timeout e snapshots. |
| `Flows.Development` | Consumir posteriormente a documentação refinada; não é chamado automaticamente pelo MVP. |

## 7. Entradas

### 7.1 Entrada documental

Por padrão, o flow lê `docs/*.md` e `docs/*.txt`, em ordem determinística. Para
evitar que uma nova execução use como fonte a própria saída anterior, arquivos
publicados pelo flow devem iniciar com o marcador:

```text
<!-- generated-by: Flows.Refinement -->
```

O leitor de entrada deve ignorar um arquivo somente quando encontrar esse
marcador no cabeçalho, entre as dez primeiras linhas; uma ocorrência no corpo ou
num exemplo de código não caracteriza documento gerado. A leitura continua
limitada por `HarnessConfig.DocsMaxChars`.

### 7.2 Entrada interativa

Quando não houver documentos de origem, ou quando houver lacunas materiais, o
prompt de descoberta deve orientar o driver a perguntar ao usuário, no mínimo:

- qual problema ou oportunidade motiva a solução;
- quem são os usuários e stakeholders;
- qual resultado de negócio é esperado;
- o que está dentro e fora de escopo;
- quais restrições, integrações, prazos ou tecnologias já são conhecidas;
- quais requisitos de segurança, privacidade ou conformidade se aplicam.

Perguntas sem impacto material podem ser registradas como pendências. Uma
decisão que altere escopo, arquitetura, risco ou conformidade não deve ser
inventada pelo agente.

## 8. Artefatos de saída

### 8.1 Área de trabalho e evidência

Durante a execução, os artefatos devem ser persistidos pelo harness:

| Ordem | Arquivo de trabalho | Finalidade |
|---|---|---|
| 0 | `.harness/discovery.md` | Contexto consolidado, fontes, fatos, premissas e dúvidas. |
| 1 | `.harness/01-project-charter.md` | Artefato da fase de Planejamento. |
| 2 | `.harness/02-software-requirements-specification.md` | Artefato da fase de Análise. |
| 3 | `.harness/03-software-design-document.md` | Artefato da fase de Design. |
| 4 | `.harness/04-readiness-handoff.md` | Revisão, rastreabilidade e passagem para desenvolvimento. |

O manifesto `.harness/artifacts.json`, já suportado por `ArtifactStore`, deve
preservar a ordem de criação.

### 8.2 Documentos publicados

Somente após aprovação da revisão, o harness publica cópias em `docs/`:

```text
docs/<slug>-01-project-charter.md
docs/<slug>-02-software-requirements-specification.md
docs/<slug>-03-software-design-document.md
docs/<slug>-04-readiness-handoff.md
```

O `<slug>` deve usar apenas letras minúsculas ASCII, números e hífens, com
limite de 64 caracteres. O caminho final deve permanecer dentro de `docs/`.

Cada arquivo publicado deve conter:

- o marcador `generated-by`;
- data e identificador do run;
- status `approved`;
- lista das fontes utilizadas;
- aviso de que o documento não representa aprovação humana quando ela não tiver
  sido explicitamente fornecida.

Arquivos de trabalho podem ser sobrescritos durante uma revisão. Arquivos
publicados só são escritos quando o conjunto passa pelo gate final.

### 8.3 Conteúdo mínimo do Project Charter

O documento de Planejamento deve conter:

- contexto e problema/oportunidade;
- stakeholders e usuários;
- objetivos identificados como `OBJ-001`, `OBJ-002`, etc.;
- métricas de sucesso identificadas como `MET-001`, `MET-002`, etc.;
- escopo e fora de escopo;
- premissas, restrições e dependências;
- análise preliminar de viabilidade técnica, operacional, econômica e temporal;
- recursos e competências esperados, sem inventar disponibilidade;
- marcos em nível de fase, sem prometer datas não informadas;
- riscos identificados como `RSK-001`, `RSK-002`, etc., com probabilidade,
  impacto, mitigação e responsável quando conhecido;
- objetivos de segurança, privacidade e conformidade;
- decisões confirmadas e questões em aberto.

### 8.4 Conteúdo mínimo da SRS

A Software Requirements Specification deve conter:

- visão do produto e fronteiras do sistema;
- glossário e atores;
- jornadas, casos de uso ou fluxos principais;
- requisitos funcionais `RF-001`, `RF-002`, etc.;
- regras de negócio `RN-001`, `RN-002`, etc.;
- requisitos não funcionais `RNF-001`, `RNF-002`, etc., com metas mensuráveis;
- requisitos de segurança e privacidade `SEC-001`, `SEC-002`, etc.;
- dados, retenção, classificação e integrações;
- interfaces externas e dependências;
- estados de erro e fluxos alternativos relevantes;
- critérios de aceite por requisito, sem implementar testes;
- premissas e questões em aberto;
- rastreabilidade de cada requisito até pelo menos um `OBJ-*`.

Termos vagos como “rápido”, “seguro”, “intuitivo” ou “escalável” não devem ser
aceitos sem critério observável ou sem marcação explícita como pendência.

### 8.5 Conteúdo mínimo do SDD

O Software Design Document deve conter:

- visão arquitetural e princípios;
- contexto do sistema e fronteiras;
- componentes e responsabilidades;
- interações, fluxos e dependências;
- modelo conceitual de dados;
- contratos e interfaces em nível de especificação, sem código executável;
- decisões arquiteturais identificadas como `ADR-001`, `ADR-002`, etc.;
- alternativas consideradas e trade-offs;
- alocação de requisitos a componentes;
- abordagem para atributos de qualidade;
- modelo preliminar de ameaças;
- controles de segurança associados a requisitos `SEC-*`;
- observabilidade e necessidades operacionais em nível de design;
- riscos técnicos e pontos que exigem prova de conceito futura;
- diagramas Mermaid quando melhorarem a compreensão;
- questões em aberto.

### 8.6 Conteúdo mínimo do handoff

O artefato de prontidão deve conter:

- inventário dos documentos e suas versões;
- resumo das decisões confirmadas;
- matriz `OBJ-* → RF/RNF/SEC-* → componente/ADR-*`;
- riscos residuais e pendências;
- conflitos encontrados e como foram resolvidos;
- decisões que ainda dependem de aprovação humana;
- fronteira explícita do que o desenvolvimento pode iniciar;
- recomendação final `READY`, com justificativa; quando o conjunto não estiver
  pronto, o flow preserva o feedback de revisão e não publica este handoff como
  aprovado;
- caminho sugerido dos documentos para o `Flows.Development`.

## 9. Requisitos funcionais do flow

| ID | Requisito |
|---|---|
| FLW-RF-001 | Iniciar um run novo a partir de documentos ou do modo interativo. |
| FLW-RF-002 | Retomar um run incompleto sem apagar estado e artefatos já produzidos. |
| FLW-RF-003 | Consolidar as fontes e respostas do usuário em um contexto de descoberta. |
| FLW-RF-004 | Produzir o Project Charter antes da SRS. |
| FLW-RF-005 | Produzir a SRS usando o contexto e o Project Charter. |
| FLW-RF-006 | Produzir o SDD usando o contexto, o Project Charter e a SRS. |
| FLW-RF-007 | Persistir cada artefato por meio do harness, sem depender da memória da conversa. |
| FLW-RF-008 | Validar formato, seções mínimas, identificadores e tamanho antes de persistir. |
| FLW-RF-009 | Revisar completude, coerência e rastreabilidade do conjunto. |
| FLW-RF-010 | Rotear uma reprovação para a fase que precisa de correção e regenerar os artefatos posteriores afetados. |
| FLW-RF-011 | Limitar revisões, passos, tamanho de instrução e duração por passo. |
| FLW-RF-012 | Publicar os documentos em `docs/` apenas depois do gate `READY`. |
| FLW-RF-013 | Registrar trace, estado final, fontes, run id e status do refinamento. |
| FLW-RF-014 | Encerrar sem publicar artefatos como aprovados quando houver bloqueio material. |
| FLW-RF-015 | Não instruir o driver a programar, testar ou implantar a solução. |

## 10. Máquina de estados proposta

```text
start
  └─> discover
        └─> planning
              └─> analysis
                    └─> design
                          └─> review ── READY ──> publish ──> stop
                                │
                                ├─ FAIL:planning ─> planning ─> analysis ─> design ─┐
                                ├─ FAIL:analysis ─────────────> analysis ─> design ─┤
                                └─ FAIL:design ───────────────────────────> design ─┘
```

`publish` é uma transição automática executada em código após `review=READY`;
não deve consumir um turno do modelo.

### 10.1 Contratos por comando

| Comando recebido | Efeito determinístico | Próxima instrução |
|---|---|---|
| `start` | Decide entre run novo e retomada; em run novo, reseta apenas os artefatos do produtor e carrega as fontes. | `discover` ou a fase pendente. |
| `discover` | Valida contexto e slug, grava `discovery.md`, run id, fontes e slug. | `planning`. |
| `planning` | Valida e grava o Project Charter. | `analysis`. |
| `analysis` | Valida e grava a SRS. | `design`. |
| `design` | Valida e grava o SDD. | `review`. |
| `review` com `READY` | Valida e grava o handoff, publica o conjunto e marca o run `completed`. | `stop`. |
| `review` com `FAIL:<fase>` | Grava feedback, incrementa a revisão e retorna à fase indicada. | `planning`, `analysis` ou `design`. |

### 10.2 Envelopes

O transporte continua usando o contrato atual:

```json
{"type":"command","value":"planning","args":["$PROJECT_CHARTER"]}
```

Para descoberta:

```json
{"type":"command","value":"discover","args":["$DISCOVERY","$PROJECT_SLUG"]}
```

Para revisão:

```json
{"type":"command","value":"review","args":["READY","$READINESS_HANDOFF"]}
```

ou:

```json
{"type":"command","value":"review","args":["FAIL:analysis","$REVIEW_FEEDBACK"]}
```

Os documentos Markdown trafegam como strings JSON; quebras de linha devem ser
escapadas como `\n`, conforme o protocolo existente.

## 11. Regras de revisão e gates

### 11.1 Gate de Planejamento

- problema, objetivos, stakeholders, escopo e fora de escopo estão explícitos;
- há ao menos um objetivo e uma métrica relacionada;
- riscos e viabilidade foram considerados;
- fatos, decisões, premissas e pendências estão diferenciados;
- segurança, privacidade e conformidade foram consideradas;
- não há compromisso inventado de prazo, orçamento ou recurso.

### 11.2 Gate de Análise

- requisitos possuem identificadores únicos;
- cada requisito é claro, necessário e verificável;
- requisitos não funcionais possuem critérios observáveis;
- fluxos principais, alternativos e erros relevantes foram considerados;
- dados, interfaces, integrações e regras de negócio estão documentados;
- todo requisito aponta para um objetivo;
- não há contradição não resolvida com o Project Charter.

### 11.3 Gate de Design

- todos os requisitos relevantes estão alocados a componentes ou decisões;
- fronteiras e responsabilidades dos componentes não se sobrepõem sem
  justificativa;
- integrações, dados e fluxos são consistentes com a SRS;
- decisões relevantes possuem alternativa e trade-off;
- ameaças e controles são coerentes com `SEC-*`;
- não há código-fonte ou atividade de implantação no artefato;
- questões técnicas ainda desconhecidas estão marcadas como riscos ou futuras
  provas de conceito.

### 11.4 Gate transversal

- IDs referenciados existem e não estão duplicados;
- a matriz de rastreabilidade não possui requisitos órfãos;
- os quatro documentos não se contradizem;
- pendências materiais impedem `READY`;
- pendências não materiais permanecem visíveis no handoff;
- o resultado respeita o escopo deste refinamento.

## 12. Revisões, bloqueios e término

- `MaxReviewCycles`: 2;
- `StepBudget`: 18;
- uma revisão de Planejamento invalida e regenera Análise e Design;
- uma revisão de Análise invalida e regenera Design;
- uma revisão de Design preserva Planejamento e Análise;
- protocolo inválido deve retornar erro corretivo, como no flow atual;
- ao exceder o limite de revisões, o flow grava status
  `needs_human_decision`, preserva os rascunhos e encerra sem publicá-los como
  `approved`;
- ao exceder o orçamento de passos ou tempo, o status final não pode ser
  confundido com sucesso;
- o adapter deve consultar `refinement_status` ao receber `stop` e comunicar
  `completed`, `needs_human_decision`, `budget_exceeded` ou `timeout`.

## 13. Estado persistido

Chaves propostas em `StateStore.Data`:

| Chave | Uso |
|---|---|
| `refinement_status` | `in_progress`, `completed`, `needs_human_decision`, `budget_exceeded` ou `timeout`. |
| `refinement_phase` | Fase atual para retomada. |
| `refinement_run_id` | GUID criado na descoberta. |
| `project_slug` | Prefixo seguro dos arquivos publicados. |
| `source_files` | Fontes utilizadas, serializadas de modo determinístico. |
| `review_cycles` | Quantidade de ciclos de correção. |
| `review_feedback` | Último feedback de revisão. |
| `revision_origin` | Fase que originou a recascata. |
| `trace_label` | `phase:discovery`, `phase:planning`, `phase:analysis`, `phase:design` ou `phase:review`. |
| `termination_reason` | Chave genérica da engine: `stop`, `budget` ou `timeout`. |

`shouldResetOnStart` deve retornar `false` enquanto
`refinement_status=in_progress`. Assim, um novo driver pode enviar `start` e
receber a instrução da fase pendente sem apagar o run.

## 14. Validações determinísticas

As validações do harness não substituem revisão semântica, mas devem rejeitar
erros baratos de detectar:

- primeiro argumento obrigatório e não vazio;
- slug no padrão `^[a-z0-9]+(?:-[a-z0-9]+)*$`;
- limites UTF-8 por artefato;
- título e seções mínimas esperadas;
- presença de ao menos um ID do tipo esperado;
- IDs duplicados no mesmo documento;
- referências a IDs inexistentes entre documentos;
- veredito de revisão no padrão
  `^(READY|FAIL:(planning|analysis|design))$`;
- ausência de placeholders não preenchidos como `{{...}}` ou `$...`;
- caminho de publicação contido em `docs/`;
- manifesto contendo exatamente os artefatos esperados e na ordem correta.

Limites iniciais sugeridos:

| Conteúdo | Limite UTF-8 |
|---|---:|
| Fontes concatenadas | `HarnessConfig.DocsMaxChars` |
| Descoberta | 12.000 bytes |
| Project Charter | 20.000 bytes |
| SRS | 40.000 bytes |
| SDD | 40.000 bytes |
| Handoff | 20.000 bytes |

Os prompts das fases posteriores devem apontar para os arquivos persistidos em
`.harness/`, em vez de reinjetar integralmente todos os artefatos no `stdout`.
Isso reduz custo e mantém os arquivos como fonte canônica.

## 15. Requisitos não funcionais

| ID | Requisito |
|---|---|
| FLW-RNF-001 | O flow deve ser compatível com `net10.0`, nullable e Native AOT. |
| FLW-RNF-002 | Toda escrita de estado, manifesto e artefato deve ser atômica. |
| FLW-RNF-003 | O flow deve ser reentrante e retomável por outro driver. |
| FLW-RNF-004 | A ordem das fontes, artefatos e transições deve ser determinística. |
| FLW-RNF-005 | O harness não deve chamar diretamente um modelo ou depender de SDK de IA. |
| FLW-RNF-006 | Nenhum caminho fornecido pelo driver pode escapar da raiz do repositório ou de `docs/`. |
| FLW-RNF-007 | Erros de protocolo devem ser corrigíveis e registrados no trace. |
| FLW-RNF-008 | Término por sucesso, bloqueio, orçamento e timeout deve ser distinguível no snapshot de estado. |
| FLW-RNF-009 | Os artefatos devem permanecer legíveis como Markdown sem ferramenta proprietária. |
| FLW-RNF-010 | O conjunto publicado deve ser idempotente para o mesmo slug e sobrescrever apenas arquivos gerados pelo próprio flow. |

## 16. Estrutura de código proposta

```text
src/dotnet/
├── Flows.Refinement/
│   ├── Flows.Refinement.csproj
│   ├── Program.cs
│   ├── RefinementTasks.cs
│   ├── RefinementTasks.Prompts.cs
│   ├── RefinementTasks.Validation.cs
│   ├── RefinementTasks.Publishing.cs
│   └── RefinementStateKeys.cs
├── Harness.Engine/
│   ├── ArtifactStore.cs              # estender publicação/limites, se necessário
│   └── DocsReader.cs                 # permitir filtro dos arquivos gerados
└── Harness.Engine.Tests/
    └── RefinementFlowTests.cs
```

Arquivos adicionais:

```text
run-refinement.sh
skills/sdlc-discovery/SKILL.md
skills/sdlc-planning/SKILL.md
skills/sdlc-analysis/SKILL.md
skills/sdlc-design/SKILL.md
skills/sdlc-review/SKILL.md
```

Cada skill deve descrever apenas a responsabilidade da fase atual. A forma
obrigatória dos artefatos deve permanecer versionada junto ao flow ou em
`ARTIFACT.md`, evitando espalhar o contrato entre prompts.

## 17. Integração com componentes existentes

### 17.1 Reutilizar

- `HarnessHost` para entrada, execução e snapshots;
- `TaskRegistry` para dispatch, orçamento, timeout e erro corretivo;
- `Envelope` e `EnvelopeValidation` para o protocolo;
- `PromptFormatter` para os contratos e skills;
- `DocsReader` para leitura limitada das fontes;
- `ArtifactStore` e `ArtifactTemplate` para persistência e padronização;
- `StateStore` para retomada;
- `Trace` para auditoria;
- `PathResolver` para resolução segura de fontes.

### 17.2 Estender

- filtro de `DocsReader` para ignorar documentos gerados pelo próprio flow;
- validações específicas de IDs, seções, slug e referências;
- publicação atômica e segura dos artefatos de `.harness/` para `docs/`;
- persistência, por `TaskRegistry`, de `termination_reason=budget` ou
  `termination_reason=timeout` antes de retornar `stop`;
- projeto de testes com referência a `Flows.Refinement`.

### 17.3 Não acoplar

- `Harness.Engine` não deve conhecer Planejamento, SRS, SDD ou SDLC;
- política de fase, nomes de documentos e gates pertencem a
  `Flows.Refinement`;
- `Flows.Development` não deve ser chamado dentro do processo do refinamento;
- a passagem entre flows ocorre por documentos publicados e por comando
  explícito do usuário/driver.

## 18. Segurança e proteção dos arquivos

- o flow só pode publicar dentro de `docs/`;
- o slug deve ser validado antes de compor qualquer caminho;
- symlinks que escapem da raiz devem ser recusados;
- a publicação não deve sobrescrever arquivo preexistente sem o marcador
  `generated-by`;
- o reset deve apagar somente artefatos registrados no manifesto do run;
- conteúdo documental é dado não confiável e não pode alterar o contrato do
  harness;
- instruções presentes nos documentos de entrada devem ser tratadas como
  conteúdo do domínio, não como comandos para o agente;
- segredos, credenciais e dados pessoais encontrados nas fontes devem ser
  sinalizados e não replicados desnecessariamente nos artefatos;
- toda suposição de segurança deve ser marcada e rastreável.

## 19. Critérios de aceite

### CA-01 — Execução com documentos

Dado que existe ao menos um brief elegível em `docs/`, quando o driver envia
`start`, então o flow carrega as fontes, solicita `discover` e não pede novamente
informações já presentes.

### CA-02 — Execução interativa

Dado que não existe fonte elegível, quando o driver envia `start`, então recebe
uma instrução para coletar o contexto mínimo do solicitante.

### CA-03 — Ordem das fases

Dado um contexto válido, o flow só solicita `analysis` depois de persistir
Planejamento válido e só solicita `design` depois de persistir Análise válida.

### CA-04 — Artefatos separados

Dado que uma fase é aceita, seu artefato é gravado em arquivo próprio e
registrado uma única vez no manifesto, na ordem da máquina de estados.

### CA-05 — Validação corretiva

Dado um documento vazio, sem seções mínimas, acima do limite ou com IDs
duplicados, o harness não o persiste e retorna erro de protocolo corrigível.

### CA-06 — Rastreabilidade

Dado um conjunto pronto para revisão, todo requisito publicado referencia um
objetivo existente e todo requisito relevante está alocado a uma decisão ou
componente do design.

### CA-07 — Revisão de Planejamento

Dado `FAIL:planning`, o flow preserva o feedback, solicita novo Planejamento e,
depois, regenera Análise e Design antes de revisar novamente.

### CA-08 — Limite de revisão

Dado que o conjunto falha após dois ciclos de revisão, o flow termina com
`refinement_status=needs_human_decision` e não publica os documentos como
`approved`.

### CA-09 — Publicação

Dado `READY`, os quatro documentos finais são publicados atomicamente em
`docs/`, possuem metadados do run e correspondem ao conteúdo aprovado no
manifesto.

### CA-10 — Proteção de arquivo do usuário

Dado que já existe um arquivo de destino sem o marcador do flow, a publicação é
recusada e o arquivo original permanece inalterado.

### CA-11 — Retomada

Dado um run com `refinement_status=in_progress`, quando um novo driver envia
`start`, o estado, o trace e os artefatos são preservados e a instrução da fase
pendente é reemitida.

### CA-12 — Limite de escopo

Durante todo o run, nenhuma instrução solicita escrita de código, criação ou
execução de testes, build, provisionamento ou implantação.

### CA-13 — Gate de segurança

Os artefatos finais contêm objetivos, requisitos e decisões de segurança
rastreáveis, ou registram explicitamente por que não se aplicam.

### CA-14 — Término observável

Ao receber `stop`, o driver consegue distinguir sucesso, bloqueio, orçamento e
timeout pelo snapshot de estado, sem inferir pelo texto do último prompt.

### CA-15 — Compatibilidade

`dotnet test` passa e `dotnet publish` do novo projeto com Native AOT não produz
warnings de serialização por reflexão.

## 20. Testes esperados

### 20.1 Unidade

- validação e normalização do slug;
- filtro de documentos gerados;
- validação de seções e tamanho UTF-8;
- detecção de IDs duplicados;
- detecção de referências órfãs;
- renderização de metadados;
- contenção do caminho de publicação;
- proteção contra sobrescrita de arquivo não gerado;
- cálculo do limite de revisões;
- resolução da fase de retomada.

### 20.2 Fluxo

- `start → discover → planning → analysis → design → review READY → stop`;
- `FAIL:planning` recascateia todas as fases posteriores;
- `FAIL:analysis` recascateia Design;
- `FAIL:design` revisa somente Design;
- terceiro `FAIL` encerra como `needs_human_decision`;
- envelope inválido não avança nem persiste;
- retomada por `start` não reseta run em andamento;
- run concluído permite um novo `start` limpo;
- publicação ocorre apenas em sucesso;
- manifesto permanece ordenado e sem duplicatas.

### 20.3 Integração

- execução pelo `run-refinement.sh` usando `.harness/inbox.json`;
- snapshots em `.harness/last-run.trace.jsonl` e
  `.harness/last-run.state.json`;
- publicação de documentos consumíveis pelo `DocsReader`;
- build Release e publicação Native AOT;
- worktree suja não é alterada fora dos arquivos pertencentes ao flow.

## 21. Backlog de implementação sugerido

| Ordem | Feature | Dependências | Resultado verificável |
|---:|---|---|---|
| 1 | Scaffold de `Flows.Refinement` e `run-refinement.sh` | — | `start` é despachado pelo novo runner. |
| 2 | Descoberta documental/interativa e filtro de arquivos gerados | 1 | `discover` persiste contexto, fontes, slug e run id. |
| 3 | Fase de Planejamento | 2 | Project Charter válido é persistido e o flow avança. |
| 4 | Fase de Análise | 3 | SRS válida e rastreável é persistida. |
| 5 | Fase de Design | 4 | SDD válido e associado aos requisitos é persistido. |
| 6 | Gate de revisão e recascata | 3, 4, 5 | `READY` conclui; `FAIL:<fase>` retorna à fase correta. |
| 7 | Publicação segura em `docs/` | 6 | Somente o conjunto aprovado é publicado atomicamente. |
| 8 | Retomada, status de término e guardas | 2, 6 | Novo driver retoma; causas de término são distinguíveis. |
| 9 | Skills e adapter do flow | 3, 4, 5, 6 | Cada etapa recebe somente a skill correspondente. |
| 10 | Testes, empacotamento e documentação de uso | 1–9 | Checks .NET e Native AOT passam; pacote inclui o flow. |

## 22. Definition of Done

O refinamento está concluído quando:

- todos os critérios de aceite deste documento estão cobertos;
- o novo projeto existe em `src/dotnet/Flows.Refinement/`;
- o runner usa o transporte por `.harness/inbox.json`;
- os quatro artefatos finais são gerados, revisados e publicados;
- retomada e causas de término estão testadas;
- nenhum passo do flow executa Programação, Testes ou Implantação;
- `dotnet test` passa;
- a publicação Native AOT passa;
- README/Quickstart explicam como iniciar e como entregar os documentos ao
  `Flows.Development`.

## 23. Decisões adotadas neste refinamento

1. O flow se chamará `Flows.Refinement`, coerente com os conceitos já presentes
   em `ArtifactStore`, `Trace`, `StateStore` e comentários do engine.
2. As fases cobertas são somente Planejamento, Análise e Design.
3. Segurança é transversal às três fases.
4. O harness, e não o agente, é responsável por persistir e publicar os
   artefatos.
5. Artefatos de trabalho ficam em `.harness/`; documentos aprovados ficam em
   `docs/`.
6. O gate pode pedir revisão, mas não pode aprovar um conjunto com decisão
   material em aberto.
7. Revisar uma fase invalida os artefatos posteriores que dependem dela.
8. A chamada ao `Flows.Development` permanece explícita e fora do processo deste
   flow.

## 24. Questões para evolução posterior

Estas questões não bloqueiam o MVP:

- portar o flow para Python e Rust após estabilizar o contrato .NET;
- permitir formatos adicionais de entrada, como PDF e DOCX, por adapters;
- gerar diagramas visuais além de Mermaid;
- adicionar aprovação humana formal por fase;
- versionar artefatos publicados sem sobrescrita;
- integrar um flow independente de avaliação dos documentos;
- iniciar opcionalmente o `Flows.Development` com o diretório de documentos
  aprovado, mediante comando explícito.
