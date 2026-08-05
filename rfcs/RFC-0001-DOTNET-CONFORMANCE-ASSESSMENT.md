# Avaliação de Conformidade — Implementação .NET vs RFC-IAO-0001

| Campo | Valor |
|---|---|
| Documento avaliado | `rfcs/0001-nist-aligned-trustworthy-execution-profile.md`, v1.0.0-draft.1 |
| Implementação avaliada | `src/dotnet/` (`Harness.Engine`, `Flows.Development`, `Harness.Engine.Tests`) |
| Commit avaliado | `HEAD` (`b8cc421`) — sem diferenças em `src/dotnet` desde o commit-baseline do RFC (`7d4fcb5`) |
| Método | Leitura estática do código-fonte contra o texto normativo do RFC; sem execução de testes adversariais (§11.2) |
| Assessor | Avaliação automatizada (Claude Code), a pedido de Yan Justino |
| Data | 2026-07-26 |
| Escopo | Apenas o engine .NET. Python e Rust não foram avaliados. |

> **Atualização (2026-07-26).** Os itens de "baixo impacto" da Seção 6.1 (escrita atômica +
> hash-chain em `StateStore`/`Trace`; containment de path sem policy externa; isolamento de
> hooks/credential-helper/pager do Git; contagem de tamanho em octetos UTF-8) foram
> **implementados** — não só em `src/dotnet`, mas de forma equivalente em `src/python` e
> `src/rust`, já que as mudanças replicam o mesmo desenho nos três engines protocol-compatible.
> As mudanças estão na árvore de trabalho local, **ainda não commitadas** (`HEAD` continua em
> `b8cc421`); os vereditos abaixo marcados com 🔧 refletem o código atual, não o commit. CI/SBOM
> (o quinto item da Seção 6.1) ficou de fora por decisão explícita — envolve chave de
> assinatura/provedor de CI, fora do escopo de uma mudança só de código. Suítes das três
> linguagens verdes após a mudança: .NET 163/163 (era 155), Python 169 (era 158), Rust 136+20
> (era 125+18).
>
> **Atualização 2 (2026-07-26).** `RunId` (RFC §6.4) foi adicionado a `RunConfig`/`run_config.json`
> nas três linguagens — gerado uma vez em `plan` (mesmo instante em que o run é reconhecido como
> genuinamente novo) e preservado em toda retomada, porque `run_config.json` já não é tocado
> quando `start` decide que há trabalho pendente. Isso fecha a parte de "identidade de run" do
> §6.4 **sem tocar no `Envelope`** trocado com o modelo — o item que a Seção 6.2 abaixo
> classificava como médio impacto ("`runId`/`messageId`/`step` assinados no envelope") foi
> reavaliado: gerar/persistir `runId` é engine-side puro (baixo impacto, feito agora); só a
> parte de anti-replay de `messageId`/`inReplyTo` — que exige validar a resposta do agente contra
> a instrução em curso — continua exigindo tocar o contrato, e segue pendente. Suítes verdes:
> .NET 166/166, Python 172, Rust 137+22.

## 1. Sumário executivo

A implementação .NET atinge o nível **IAO Core** (§10.1): protocolo funcional com estados
determinísticos, esquema versionável, budgets de passos/custo/tempo, persistência e geração de
trace, mais uma suíte de testes ampla (35 arquivos de teste). Isso está bem executado e é
consistente com o que o próprio RFC já reconhece na Seção 4.

Ela **não atinge IAO Controlled** (§10.2). Dos oito blockers originalmente identificados, um
foi fechado e três reduzidos a "parcial" pelas duas atualizações de 2026-07-26 (🔧); os quatro
restantes seguem sem controle equivalente no código:

1. **Política mutável pelo workspace** — `harness.json` é lido do mesmo diretório que o agente supervisiona. *(inalterado)*
2. 🔧 **Containment de path parcial** — `PathResolver` agora fecha o desvio por symlink e `ResolveTargetDir` rejeita `/`/home/diretório do harness; ainda falta containment contra uma raiz de política assinada arbitrária (exige o capability broker da Fase 2).
3. **Verificação autoatestada** — `verify-feature.sh` roda de dentro do próprio alvo controlado pelo agente. *(inalterado)*
4. **Auto-aprovação de efeitos de alto impacto** — `bypassPermissions` e `autoApprove` são defaults empacotados. *(inalterado — fora do escopo desta rodada por decisão explícita, ver Seção 6.3)*
5. **Commit automático sem aprovação** — `git add -A` + `git commit` roda sem gate humano. *(inalterado — hooks agora isolados, mas o gate de aprovação continua ausente)*
6. ✅ **Hooks de Git neutralizados** — `GitCommand.Run`/equivalentes agora injetam `-c core.hooksPath=`/`credential.helper=`/`core.pager=cat` em toda invocação, nas três linguagens.
7. 🔧 **Estado e evidência com integridade parcial** — `StateStore`/`Trace` (e os demais stores JSON) agora escrevem atomicamente (temp+rename) e `trace.jsonl` tem hash-chain (SHA-256 da linha anterior); ainda falta número de geração, lease e assinatura.
8. 🔧 **Identidade de run parcial** — `runId` agora existe (gerado em `plan`, persistido em `run_config.json`, sobrevive à retomada); o envelope continua sem `messageId`/`step` assinado/`policyDigest`, então não há anti-replay real da resposta do agente.

**IAO Assured** (§10.3) está ainda mais distante: exige assinatura digital, verificador
isolado, builds reproduzíveis e ausência total do modo de envelope legado — nenhuma dessas
primitivas existe no código.

Um ponto estrutural que vale destacar antes da tabela: o `ARCHITECTURE.md` da raiz do projeto
declara *explicitamente* que sandbox de execução, permissões e cliente de modelo são
**"Absent by design"** — atribuídos à IDE/driver, não ao harness. Essa é uma decisão de
arquitetura deliberada, não uma omissão. Só que ela está em tensão direta com a Seção 6.9 do
RFC, que exige que o *harness* (control plane) medeie esses efeitos via um capability broker —
não que ele delegue essa mediação à IDE, que é, pela própria definição de fronteira de
confiança do RFC (§3), parte do que está fora da fronteira de confiança junto com o agente.

## 2. Avaliação controle-a-controle (RFC §6)

| # | Requisito (RFC) | Veredito | Evidência em `src/dotnet` | Observação |
|---|---|---|---|---|
| 6.1 | Separação de planos (policy/control/exchange/target) | **Não atende** | Não há distinção de processo/permissão entre planos; tudo roda no mesmo processo com o mesmo acesso a disco | `.harness` é ao mesmo tempo canal de troca e "estado autoritativo" |
| 6.2 | Política imutável e assinada antes da 1ª instrução | **Não atende** | `HarnessConfig.Load()` lê `harness.json` do CWD sem assinatura, digest ou fonte externa | `HarnessConfig.cs:58-79` |
| 6.2 | Config do workspace só pode restringir, nunca expandir | **Parcialmente atendido** | `timeoutMs` tem teto de 5 min, não pode ser desligado por `0`/negativo e timeout/budget deixam um latch terminal persistente; `MaxSteps`/`MaxInstructionChars` ainda podem ser ampliados pelo workspace | `HarnessConfig.cs`, `TaskRegistry.cs`, `StateStore.cs` |
| 6.3 | Path authorization (containment canônico, symlink, rejeição de `/`/home) | 🔧 **Parcial** | `PathResolver.Resolve` agora resolve o alvo final do symlink e rejeita se ele escapar da base (CWD/diretório do binário) contra a qual foi resolvido; ainda não há containment contra uma raiz de política assinada arbitrária | `PathResolver.cs:10-73` |
| 6.3 | Mesmo check para target/temp/log/artifact/Git | 🔧 **Parcial** | `ResolveTargetDir` agora rejeita path vazio, raiz do sistema, home do usuário e diretório de instalação do harness (lista mínima do §6.3); não deriva de uma política externa nem cobre temp/log/artifact separadamente | `DevelopmentTasks.Handoff.cs:104-124`, `DevelopmentTasks.Verify.cs:16-34` (chamador agora trata a rejeição sem derrubar o processo) |
| 6.4 | `runId` global único, `messageId`, `step` monotônico assinado | 🔧 **Parcial** | `RunConfig.RunId` (`Guid.NewGuid()`) agora é gerado uma vez em `Plan()` e persistido em `run_config.json`, sobrevivendo a toda retomada; `step` já era monotônico internamente via `StateStore.Increment()` (nunca confiava em valor vindo do agente). O `Envelope` continua sem `messageId`/`inReplyTo` assinado — nada valida se uma resposta do agente é "a atual" | `RunConfigStore.cs:69-78`, `DevelopmentTasks.cs:80-86`, `Envelope.cs:15-25` |
| 6.4 | Lease de escritor único, geração, expiração | **Não atende** | Nenhum mecanismo de lease; múltiplos processos podem escrever `.harness/state.json` concorrentemente | — |
| 6.5 | Envelope versionado (`schema`, `policyDigest`, `integrity`) | **Não atende** | O envelope implementado é exatamente o "legacy `{type,value,args,context}`" que o RFC diz que "MAY be accepted only in compatibility mode" e "MUST NOT qualify for the Assured conformance level" | `Envelope.cs:15-18` |
| 6.6 | Digest SHA-256 + MAC + hash-chain em mensagens/evidência | 🔧 **Parcial** | `trace.jsonl` agora tem hash-chain (SHA-256 hex da linha anterior, campo `prevHash`, gênese = 64 zeros); cobre só a evidência de trace, não mensagens do envelope, e não há MAC/chave — um adulterador com acesso de escrita ainda pode recalcular a cadeia inteira | `Trace.cs:68-99` |
| 6.7 | Estado transacional: atômico, gerado, protegido por lease, verificável | 🔧 **Parcial** | `StateStore.Save`/`RunConfigStore`/`FeatureStore`/`ArtifactStore` agora escrevem via `AtomicIO.WriteAllTextAtomic` (temp file no mesmo diretório + `File.Move` atômico); ainda falta número de geração e lease/lock | `StateStore.cs:50-61`, `AtomicIO.cs` |
| 6.7 | Falha de persistência MUST produzir `control_failure`, nunca estado vazio | **Não atende** | Todo `catch` em `StateStore`/`Trace`/`RunConfigStore`/`FeatureStore`/`ArtifactStore`/`ScoreStore` loga em stderr e devolve default/lista vazia — o padrão "fail-open" é consistente em todos os stores | `StateStore.cs:42-47`, `Trace.cs:96-101`, `FeatureStore.cs:125-129`, etc. |
| 6.8 | Manifesto de input ativo, classificação de proveniência, limites por fonte | **Não atende** | `DocsReader.Read` concatena **todo** `*.md`/`*.txt` de uma pasta sem manifesto explícito, sem digest, sem classe de confiança por arquivo | `DocsReader.cs:33-70` |
| 6.8 | Teto de bytes agregado com truncamento em fronteira UTF-8 registrado | 🔧 **Parcial** | O teto (`DocsMaxChars`) agora é medido e cortado em octetos UTF-8 reais (`Encoding.UTF8.GetByteCount`/`TruncateUtf8Bytes`, fronteira de byte líder válida); ainda só avisa em stderr, não é registrado no audit trail estruturado | `DocsReader.cs:19-90` |
| 6.8 | Skills allowlisted com digest correspondente à política | **Não atende** | `PromptFormatter.Skills`/`ReadSkills` inlina qualquer skill por nome/caminho, sem checagem de digest | `PromptFormatter.cs:8-16,46-76` |
| 6.9 | Capability broker medeia todo efeito nomeado (fs/net/git/process/secret) | **Não atende** | Chamadas diretas a `Process`/`GitCommand` a partir dos flows, sem broker central; `ARCHITECTURE.md` atribui essa responsabilidade à IDE, não ao harness | `GitCommand.cs`, `DevelopmentTasks.Verify.cs:42-93` |
| 6.9 | Rede negada por padrão | **N/A no engine, mas delegado sem controle** | O harness não faz chamadas de rede diretamente, mas também não impõe política de egress sobre o que o agente/IDE faz | — |
| 6.9 | Subprocessos com ambiente mínimo, sem credenciais desnecessárias | 🔧 **Parcial** | `GitCommand`/`Process` de verify usam `ArgumentList` (evita shell injection), têm timeout com `Kill(entireProcessTree: true)`, e agora `GitCommand.Run` bloqueia `credential.helper` via `-c` em toda invocação; ainda herdam o restante do ambiente do processo pai (sem `EnvironmentVariables.Clear()`) | `GitCommand.cs:11-45`, `DevelopmentTasks.Verify.cs:44-93` |
| 6.10 | Classificação de efeitos A0–A3 com aprovação por classe | **Não atende** | Não existe essa classificação no código; commit (A2) e potencialmente push são tratados como efeitos automáticos | `DevelopmentTasks.Handoff.cs:71-74` |
| 6.10 | Auto-approval/bypass NÃO deve ser default de pacote | **Não atende** | `.claude/settings.json:3` → `"defaultMode": "bypassPermissions"`; `.vscode/settings.json:8` e `package.sh:353-366` geram `chat.tools.terminal.autoApprove` | — |
| 6.11 | Neutralização de hooks Git, pathspec derivado, scan de segredo antes do commit | 🔧 **Parcial** | `GitCommand.Run` agora injeta `-c core.hooksPath=<dir vazio>` em toda invocação, neutralizando hooks do repositório-alvo; `git add -A -- . :(exclude).harness` continua sem ser derivado de um change-set autorizado (exige o capability broker), e não há scan de segredo | `GitCommand.cs:14-45`, `DevelopmentTasks.Handoff.cs:56-58` |
| 6.11 | Commit requer autorização A2 ou pré-autorização explícita | **Não atende** | Commit roda automaticamente ao fim de cada feature verificada, sem gate | `DevelopmentTasks.Handoff.cs:71-74` |
| 6.12 | Verificação não pode depender só de script editável pelo agente | **Não atende** | `verify-feature.sh` é o único gate de verificação e vive dentro do `targetDir`; o resultado alimenta diretamente `FeatureStore.MarkPassed` | `DevelopmentTasks.Verify.cs:15-40`, `DevelopmentTasks.Handoff.cs:22-26` |
| 6.12 | Veredito rotulado `self_attested` quando vem de script editável | **Não atende** | O resultado de `verify-feature.sh` é tratado como PASS/FAIL definitivo, sem rótulo de proveniência | `DevelopmentTasks.Verify.cs:34-39` |
| 6.13 | Stream de auditoria com hash-chain, digests de política/implementação, decisão de autorização | 🔧 **Parcial (hash-chain implementado; digests/decisão de autorização ainda ausentes)** | `trace.jsonl` agora encadeia cada linha ao SHA-256 da anterior (`prevHash`), tornando edição/remoção retroativa detectável; ainda falta digest de política/implementação e decisão de capability (não há capability broker) | `Trace.cs:50-99` |
| 6.13 | Timestamps RFC 3339 UTC | **Atende** | `TraceEntry.Timestamp` usa `DateTimeOffset.UtcNow`, serializado por `System.Text.Json` em formato ISO 8601/RFC 3339 | `Trace.cs:53` |
| 6.14 | Telemetria de sessão opt-in, minimizada, redigida | **Fora de `src/dotnet`** | Os scripts de uso/custo citados no RFC (`scripts/*_usage.py`) não fazem parte de `src/dotnet` — não avaliados aqui, mas o gap apontado pelo RFC (§4.4, linha "Evidence can contain sensitive local transcripts") permanece válido para o produto como um todo |
| 6.15 | SBOM, scan de dependência, build assinado, CI protegido | **Não atende** | `find .github` só retorna `.github/prompts/`; não existe `.github/workflows`, nem script de SBOM/assinatura em `src/dotnet` | — |
| 6.16 | Semântica cross-engine unificada (octetos UTF-8, JSON canônico, timeout process-tree) | 🔧 **Parcial** | `DocsReader`, o custo de instrução (`TaskRegistry`/`StateStore.AddCost`) e os truncamentos de snippet/commit message agora medem e cortam em octetos UTF-8 nas três linguagens (Rust já estava correto em `task_registry.rs`, .NET e Python foram corrigidos); JSON canônico (RFC 8785) e paridade de semântica de timeout/process-tree entre engines ainda não foram verificados | `DocsReader.cs:19-90`, `TaskRegistry.cs:58-71` |

## 3. Registro de risco cruzado (RFC §5.3)

| Risco | Rating do RFC | Status em `src/dotnet` | Evidência |
|---|---|---|---|
| IAO-R01 — Prompt injection muda a hierarquia de instrução | Crítico | **Aberto** | `DocsReader` concatena todo `specs/*.md`/`*.txt` sem rótulo de confiança; `PromptFormatter` inlina skills sem digest |
| IAO-R02 — Path do agente escapa da raiz autorizada | Crítico | 🔧 **Parcialmente mitigado** | `PathResolver` fecha escape por symlink; `ResolveTargetDir` rejeita `/`/home/dir do harness. Containment contra uma raiz de política assinada arbitrária ainda exige o capability broker (Fase 2) — permanece Crítico até lá |
| IAO-R03 — Verificador controlado pelo agente aprova falsamente | Crítico | **Aberto** | `verify-feature.sh` roda de dentro do `targetDir`, é o único gate |
| IAO-R04 — Tampering de estado/trace esconde run inseguro | Alto | 🔧 **Parcialmente mitigado** | `trace.jsonl` agora tem hash-chain SHA-256 — edição/remoção retroativa de uma linha quebra a cadeia a partir dali. `state.json` ganhou escrita atômica (evita corrupção por crash) mas não hash-chain/assinatura — segue sem detectar edição deliberada |
| IAO-R05 — Efeitos auto-aprovados excedem autoridade pretendida | Alto | **Aberto** | `bypassPermissions`/`autoApprove` como default de pacote; commit sem gate |
| IAO-R06 — Relatórios de sessão vazam segredos/dados pessoais | Alto | **Fora de `src/dotnet`** | Scripts de report não fazem parte do engine .NET avaliado |
| IAO-R07 — Dependência/pacote comprometido entra em release | Alto | **Aberto** | Sem lockfile enforcement visível em CI, sem SBOM, sem CI (`.github/workflows` ausente) |
| IAO-R08 — Drivers concorrentes corrompem/bifurcam um run | Alto | **Aberto** | Sem lease; dois processos podem escrever `.harness/state.json` ao mesmo tempo sem lock |
| IAO-R09 — Diferenças de runtime quebram equivalência de protocolo/evidência | Moderado | 🔧 **Parcialmente mitigado** | A divergência específica citada (contagem de tamanho) foi fechada: as três engines agora medem specs/custo de instrução/snippets em octetos UTF-8. Outras fontes de divergência do Apêndice B (JSON canônico, ordenação de erro, semântica de timeout) seguem abertas |
| IAO-R10 — Config do workspace desliga budgets/aumenta timeouts | Alto | **Parcialmente mitigado** | `timeoutMs` não pode ser desligado, é limitado a 5 min e um timeout/budget não pode ser reaberto em nova invocação; `MaxSteps`/`MaxInstructionChars` **não** têm teto externo equivalente — o workspace pode aumentá-los livremente |
| IAO-R11 — Falha de persistência tratada como estado vazio válido | Alto | **Aberto** | Padrão fail-open inalterado em todos os stores. A escrita agora ser atômica evita *corrupção* por crash no meio de uma escrita, mas não muda o que acontece quando a *leitura*/escrita falha por outro motivo — o `catch` ainda devolve default silenciosamente. Fail-closed foi deixado fora desta rodada por decisão explícita (ver Seção 6, item de alto impacto) |
| IAO-R12 — Testes funcionais verdes escondem ausência de testes de segurança adversariais | Alto | 🔧 **Parcialmente mitigado** | A suíte ganhou testes pontuais para os cenários que a mudança fechou (symlink escape em `PathResolverTests`, adulteração de hash-chain em `TraceTests`, flags de isolamento em `GitCommandTests`, truncamento UTF-8 em `DocsReaderTests`, equivalentes em Python/Rust). Ainda cobre só uma fração dos 25 cenários adversariais do §11.2 — replay, tampering de política, hook malicioso *de fato* executando, etc. seguem sem teste |

## 4. Conformidade da máquina de estados (RFC §7)

- **Estados tipados (§7.1):** o RFC exige distinguir `created`, `policy_validated`, `planning`,
  `ready`, `implementing`, `verifying`, `awaiting_approval`, `committing`, `completed`,
  `cancelled`, `budget_exhausted`, `timed_out`, `control_failure`, `security_hold`,
  `incident_hold`. A implementação usa um único literal `"stop"` como sinal de término em
  `HarnessHost.Run` (`HarnessHost.cs:25-33`), diferenciado apenas no *trace* por
  `TraceOutcome` (`instruction`/`stop`/`error`/`budget`/`timeout` — `Trace.cs:117-122`). Isso é
  exatamente o caso que o RFC descreve como insuficiente: "The literal `stop` is insufficient
  as an authoritative terminal result because it conflates success, budget exhaustion,
  timeout, blocked dependencies, and failure." **Não atende.**
- **Validação de planejamento (§7.2.2):** `FeatureStore.Parse`/`DependencyGraphError` valida
  unicidade de id (via reindex) e ausência de ciclo/dangling ref (Kahn) antes de aceitar a
  lista de features — **atende parcialmente** (falta validação de tamanho/escopo do payload).
  Boa evidência positiva, citada no RFC (§4.3) como propriedade já existente. `FeatureStore.cs:45-112`.
- **Feature não pode entrar em `implementing` sem dependências verificadas (§7.2.3):**
  `FeatureStore.NextPending` só libera uma feature quando todas as `Deps` já têm `Passes ==
  true` — **atende** no nível de sequenciamento, mas "verificado" aqui significa apenas
  "`verify-feature.sh` retornou exit 0", que por sua vez é autoatestado (ver §6.12 acima) —
  então o encadeamento é correto, mas a raiz da cadeia não é confiável.
- **Commit não pode ocorrer antes de aprovação (§7.2.6):** **não atende** — commit é automático
  ao fim de `TryAutomatedHandoff` (`DevelopmentTasks.Handoff.cs:71-74`).
- **Budget exhaustion/timeout não podem ser representados como sucesso (§7.2.8):** **atende** —
  `TaskRegistry.Resolve` retorna `"stop"` tanto em sucesso quanto em budget/timeout, mas o
  *trace* grava o outcome real (`TraceOutcome.Budget`/`Timeout`), então a distinção sobrevive
  na evidência mesmo que não no valor de retorno do processo (`TaskRegistry.cs:79-93,124-128`).

## 5. Roteiro de remediação (ancorado nas Fases 0–5 do RFC, §12)

Cada item referencia o gap correspondente na Seção 2.

**Fase 0 — Especificação e freeze.** Não requer mudança em `src/dotnet` ainda; é definição de
schema/ADR compartilhada entre os três engines. Prioridade: acordar o novo formato de
`Envelope` (schema v1 do §6.5) antes de tocar em qualquer store.

**Fase 1 — Integridade do control plane.**
- Substituir `HarnessConfig.Load()` por um manifesto de política assinado, resolvido de uma
  raiz protegida fora do CWD do agente (`HarnessConfig.cs`). *(pendente)*
- 🔧 Reescrever `PathResolver` e `ResolveTargetDir` como um único componente de containment
  canônico: resolver symlinks, validar contra uma lista de raízes autorizadas, rejeitar
  `/`/home (`PathResolver.cs`, `DevelopmentTasks.Handoff.cs:88-92`). **Feito parcialmente**
  (2026-07-26, nas três linguagens): symlink escape fechado e lista mínima de rejeição
  (`/`/home/dir do harness) implementada; falta validar contra uma lista de raízes vinda de
  política externa.
- ✅ Adicionar `runId`/`messageId`/`step` assinado ao `Envelope` e um lease de escritor único
  sobre `.harness/state.json` (`Envelope.cs`, `StateStore.cs`). **`runId` feito** (2026-07-26,
  nas três linguagens) — mas em `RunConfig`/`run_config.json`, não no `Envelope` (ver §6.2).
  `messageId`/`step` assinado no envelope e o lease de escritor único continuam pendentes.
- ✅ Reescrever `StateStore.Save`/`Trace.Append` para escrita atômica (arquivo temporário no
  mesmo filesystem + rename) com hash do evento anterior (`StateStore.cs:50-61`,
  `Trace.cs:48-99`). **Feito** (2026-07-26, nas três linguagens) — falta só o número de
  geração explícito.
- Trocar o padrão fail-open por fail-closed: falha de persistência autoritativa deve produzir
  um estado `control_failure` explícito, não devolver default silenciosamente — afeta todos os
  `catch` genéricos em `StateStore`, `FeatureStore`, `RunConfigStore`, `ArtifactStore`,
  `ScoreStore`. *(pendente — deixado fora de propósito, é o item de "fail-closed" classificado
  como alto impacto na Seção 6)*

**Fase 2 — Capability broker e aprovação.**
- Introduzir um broker central que intercepta toda chamada a `GitCommand.Run` e ao `Process`
  de `DevelopmentTasks.Verify.cs`, classifica o efeito em A0–A3 e aplica a regra de aprovação
  correspondente antes de executar. *(pendente — alto impacto, Seção 6.3)*
- Trocar `git add -A -- . :(exclude).harness` por pathspec derivado do change-set autorizado
  *(pendente — exige o broker acima)*; ✅ adicionar `-c core.hooksPath=<vazio>` (mais
  `credential.helper=`/`core.pager=cat`) em todo `GitCommand.Run`, para neutralizar hooks do
  repositório-alvo (`GitCommand.cs`, `DevelopmentTasks.Handoff.cs:56-74`). **Feito**
  (2026-07-26, nas três linguagens).
- Remover `"defaultMode": "bypassPermissions"` de `.claude/settings.json` e o
  `chat.tools.terminal.autoApprove` gerado por `package.sh`/`.vscode/settings.json` como
  default de pacote; se unattended operation for desejado, exigir uma política assinada que
  enumere exatamente as capacidades pré-autorizadas (§6.10). *(pendente — alto impacto, Seção 6.3)*

**Fase 3 — TEVV independente e proveniência de prompt.**
- Parar de tratar o exit code de `verify-feature.sh` como veredito autoritativo em
  `TryAutomatedVerify`/`CompleteVerifiedFeature`; rotular explicitamente como
  `self_attested` e introduzir um verificador que roda fora do `targetDir` controlado pelo
  agente, sobre uma cópia isolada da árvore (`DevelopmentTasks.Verify.cs`, `DevelopmentTasks.Handoff.cs:13-27`).
- Substituir a leitura indiscriminada de `specs/*.md`/`*.txt` em `DocsReader.Read` por um
  manifesto de input ativo explícito (path, digest, classe de confiança, byte count) e aplicar
  o mesmo tratamento a `PromptFormatter.ReadSkills` (digest de skill contra allowlist da
  política) (`DocsReader.cs`, `PromptFormatter.cs`).

**Fase 4 — Privacidade e supply chain.**
- Criar `.github/workflows` com build, teste, SAST/SCA/secret scan e geração de SBOM para o
  projeto .NET (hoje inexistente).
- Assinar os artefatos de release e publicar informação de verificação; pinar versão de SDK
  .NET/pacotes usados no build.

**Fase 5 — Conformidade Assured.**
- ✅ Unificar medição de tamanho para octetos UTF-8 em `DocsReader` e no custo de instrução
  (antes usavam `string.Length`/`len()`/`.chars().count()`, unidades divergentes entre
  runtimes), fechando o Apêndice B item 1 do RFC. **Feito** (2026-07-26, nas três linguagens;
  Rust já estava correto em `task_registry.rs`). `EnvelopeValidation` continua sem teto de
  tamanho — não fazia parte do escopo desta rodada.
- Construir a suíte de conformidade cross-engine (resume .NET↔Python↔Rust) e os 25 testes
  adversariais do §11.2 — a suíte ganhou alguns testes pontuais (symlink escape, tampering de
  hash-chain, flags de isolamento do Git, truncamento UTF-8) mas a suíte formal de conformidade
  cross-engine e a maioria dos 25 cenários adversariais seguem pendentes.

## 6. Análise de impacto no core do padrão

Nem todo item do roteiro da Seção 5 tem o mesmo custo. Alguns são hardening interno,
invisível ao loop; outros revertem decisões de design que o próprio `ARCHITECTURE.md` assume
deliberadamente e mudam o que é sentido no uso diário. Esta seção classifica cada item por
impacto sobre o comportamento atual, não sobre segurança (que já está coberta nas Seções 2-3).

### 6.1 Baixo impacto — internos, não mudam a experiência

Ficam "atrás" do contrato atual; não alteram como o agente/driver interage com o harness:

| Item (Fase) | Por que é de baixo impacto | Status |
|---|---|---|
| Escrita atômica + hash-chain em `StateStore`/`Trace` (F1) | Troca `File.WriteAllText` por temp+rename; nenhuma mudança observável de comportamento | ✅ Implementado 2026-07-26 (.NET/Python/Rust) |
| Path containment em `PathResolver` (F1) | Só passa a rejeitar o que hoje já seria um bug (escape de diretório); não afeta uso legítimo | ✅ Implementado 2026-07-26 (.NET/Python/Rust) — versão sem policy externa, ver §6.3 acima |
| Neutralizar hooks + pathspec em vez de `git add -A` (F2) | Muda como `GitCommand`/`Handoff` monta o comando, não muda quando/por que ele commita | 🔧 Hooks neutralizados 2026-07-26 (.NET/Python/Rust); pathspec derivado do change-set continua pendente (exige o broker) |
| Contagem em octetos UTF-8 (F5) | Correção de unidade de medida, sem efeito no fluxo | ✅ Implementado 2026-07-26 (.NET/Python/Rust) |
| `runId` em `run_config.json` (F1) | Reclassificado de médio para baixo impacto (ver §6.2 abaixo): gerado/persistido pelo harness, o modelo nunca vê nem precisa ecoar o campo | ✅ Implementado 2026-07-26 (.NET/Python/Rust) |
| CI/SBOM/assinatura de release (F4) | Infraestrutura em volta do produto, não toca o loop | Pendente — decisão explícita de deixar fora desta rodada (chave de assinatura/provedor de CI) |

Os quatro primeiros itens (exceto a parte de pathspec/broker) foram implementados em
2026-07-26 — ver nota no topo do documento. Confirma a leitura original: nenhuma suíte de
teste quebrou, nenhum fluxo do agente mudou de comportamento observável.

### 6.2 Médio impacto — adiciona fricção, mas o loop continua reconhecível

| Item (Fase) | Custo introduzido |
|---|---|
| Política assinada fora do CWD do agente (F1) | Exige gestão de chave/config externa, mas é lida uma vez por run; não muda o ciclo turno-a-turno |
| `messageId`/`inReplyTo` como anti-replay real da resposta do agente (F1) | ~~`runId`/`messageId`/`step` assinados no envelope~~ — **reavaliado em 2026-07-26**: `runId` e `step` não precisavam do envelope (ver nota de atualização no topo e §6.1 acima, já implementado). O que sobra é mais estreito: rejeitar uma resposta do agente que não é "a atual" (driver reenviando uma resposta antiga em cache) exige que o harness declare, na instrução emitida, algum token de correlação e valide o que volta contra ele. Hoje o contrato é `{type, value, args}`, deliberadamente mínimo para um modelo barato acertar o JSON de primeira (ver `PromptFormatter.cs:26-34`, `EnvelopeValidation.cs`); qualquer campo que o modelo precise ecoar de volta é mais uma chance de quebrar o parse. Mitigável fazendo o harness validar de forma transparente (sem o modelo precisar "pensar" no campo), mas ainda é engenharia adicional real, e o valor é menor do que parecia — no processo síncrono de hoje (um processo por turno), não há concorrência real dentro de uma mesma sessão de driver; o cenário que isso protege é estreito (driver com bug/malicioso reenviando cache) |

### 6.3 Alto impacto — mudam o valor central do padrão

Estes três não são hardening: são reversão de decisões de design explícitas do projeto.

1. **Remover `bypassPermissions`/auto-approve como default (F2).** O motivo de existir do IAO
   — conforme `README.md` — é permitir um loop **longo e desacompanhado**, que sobrevive a
   resets de contexto e termina features sem um humano aprovando cada terminal/commit. Exigir
   aprovação A2/A3 a cada commit/efeito de terminal transforma "agente autônomo que trabalha
   enquanto você faz outra coisa" em "agente que para e espera você o tempo todo" — não é um
   ajuste, é remover a proposta de valor central.
2. **Capability broker mediando fs/rede/git/processo (F2).** O `ARCHITECTURE.md` declara
   textualmente que sandbox/permissão são *"Absent by design — pertence à IDE, não ao
   harness"*. Construir um broker de capacidades dentro do harness desfaz essa fronteira
   arquitetural de propósito, reintroduzindo exatamente a complexidade que o padrão foi
   desenhado para não carregar.
3. **Verificador independente, isolado do `targetDir` (F3).** Hoje `verify-feature.sh` roda
   in-place, rápido, na mesma working tree (`DevelopmentTasks.Verify.cs:20-22`). Isolar a
   verificação implica copiar a árvore, reinstalar dependências, rodar em outro lugar — cada
   feature passa a pagar esse custo de infraestrutura e tempo, atacando diretamente a promessa
   de "loop barato e rápido, zero-token no gate determinístico".

Um quarto ponto no limite entre médio e alto impacto: **fail-closed em vez de fail-open (F1)**
reverte uma escolha comentada em quase todo store do código ("config/estado é insumo opcional,
não pode derrubar o run" — ver comentários em `HarnessConfig.cs:9-10`, `StateStore.cs`,
`FeatureStore.cs:14-15`). Isso troca resiliência a falha transiente (disco, lock de
antivírus, sync de nuvem) por auditabilidade — em uso local de desenvolvimento, provavelmente
aumenta paradas espúrias do run.

### 6.4 Leitura geral

O próprio RFC separa **Core** (o que existe hoje, "suitable for experimentation") de
**Controlled/Assured** (perfil para operação supervisionada/empresarial) em vez de propor um
único alvo (§10) — o que sugere que nem o RFC espera que Core vire Controlled sem custo.
Recomendação: adotar os itens de 6.1 sem debate; decidir os itens de 6.3 (e o fail-closed de
6.2/6.3) como uma escolha explícita de produto — "harness que roda sozinho" vs. "harness
auditável/supervisionado" — antes de qualquer código, porque não são bugs a corrigir, são uma
mudança na proposta de valor.

## 7. Fora de escopo desta avaliação

- A avaliação formal (tabelas das Seções 2 e 3) cobre só `src/dotnet`; o RFC é cross-engine e o
  pedido original foi especificamente sobre `src/dotnet`. A atualização de 2026-07-26 implementou
  o mesmo desenho de hardening em `src/python` e `src/rust` (ver nota no topo do documento), mas
  isso é evidência de implementação replicada, não uma avaliação de conformidade independente
  desses dois engines — eles podem ter gaps adicionais específicos de linguagem não cobertos aqui.
- Não foi executado nenhum dos 25 testes adversariais do §11.2 (path escape, replay, tampering,
  hook malicioso, etc.) — esta é uma leitura estática de código contra o texto normativo, não
  uma auditoria de penetração ou um assessment formal nos termos do §11 (que exige exame,
  entrevista e teste).
- Os scripts de telemetria de uso/custo (`scripts/*_usage.py`) citados no RFC como fonte do
  risco IAO-R06 não fazem parte de `src/dotnet` e não foram avaliados aqui.
