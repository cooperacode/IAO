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

## 1. Sumário executivo

A implementação .NET atinge o nível **IAO Core** (§10.1): protocolo funcional com estados
determinísticos, esquema versionável, budgets de passos/custo/tempo, persistência e geração de
trace, mais uma suíte de testes ampla (35 arquivos de teste). Isso está bem executado e é
consistente com o que o próprio RFC já reconhece na Seção 4.

Ela **não atinge IAO Controlled** (§10.2). Nenhum dos oito blockers abaixo tem controle
equivalente no código hoje:

1. **Política mutável pelo workspace** — `harness.json` é lido do mesmo diretório que o agente supervisiona.
2. **Sem containment de path** — `PathResolver`/`ResolveTargetDir` resolvem caminho absoluto sem checar raiz autorizada.
3. **Verificação autoatestada** — `verify-feature.sh` roda de dentro do próprio alvo controlado pelo agente.
4. **Auto-aprovação de efeitos de alto impacto** — `bypassPermissions` e `autoApprove` são defaults empacotados.
5. **Commit automático sem aprovação** — `git add -A` + `git commit` roda sem gate humano.
6. **Hooks de Git não neutralizados** — `GitCommand` não isola hooks/credential-helper.
7. **Estado e evidência sem integridade** — sem escrita atômica, geração, hash-chain ou assinatura em nenhum store.
8. **Sem identidade de run/anti-replay** — o envelope não tem `runId`, `messageId`, `step` assinado ou `policyDigest`.

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
| 6.2 | Config do workspace só pode restringir, nunca expandir | **Parcial** | `MaxAllowedTimeoutMs` é um teto hard-coded que `harness.json` não pode ultrapassar — é o único caso desse tipo no código | `HarnessConfig.cs:40` (positivo); demais campos (`MaxSteps`, `MaxInstructionChars`) não têm teto externo equivalente |
| 6.3 | Path authorization (containment canônico, symlink, rejeição de `/`/home) | **Não atende** | `PathResolver.Resolve` faz só `Path.GetFullPath`; nenhuma checagem de raiz, nenhuma resolução de symlink para containment | `PathResolver.cs:10-21` |
| 6.3 | Mesmo check para target/temp/log/artifact/Git | **Não atende** | `ResolveTargetDir` idem — apenas `Path.GetFullPath(configured)` | `DevelopmentTasks.Handoff.cs:88-92` |
| 6.4 | `runId` global único, `messageId`, `step` monotônico assinado | **Não atende** | `Envelope` tem só `Type`, `Value`, `Args`, `Context` (formato legado explícito do §6.5) | `Envelope.cs:15-25` |
| 6.4 | Lease de escritor único, geração, expiração | **Não atende** | Nenhum mecanismo de lease; múltiplos processos podem escrever `.harness/state.json` concorrentemente | — |
| 6.5 | Envelope versionado (`schema`, `policyDigest`, `integrity`) | **Não atende** | O envelope implementado é exatamente o "legacy `{type,value,args,context}`" que o RFC diz que "MAY be accepted only in compatibility mode" e "MUST NOT qualify for the Assured conformance level" | `Envelope.cs:15-18` |
| 6.6 | Digest SHA-256 + MAC + hash-chain em mensagens/evidência | **Não atende** | Zero ocorrências de `sha256`/`hmac`/`signature`/`hashChain` em `src/dotnet/**/*.cs` (busca literal confirmada) | — |
| 6.7 | Estado transacional: atômico, gerado, protegido por lease, verificável | **Não atende** | `StateStore.Save` faz `File.WriteAllText` direto (sem temp+rename); sem número de geração; sem lock | `StateStore.cs:50-61` |
| 6.7 | Falha de persistência MUST produzir `control_failure`, nunca estado vazio | **Não atende** | Todo `catch` em `StateStore`/`Trace`/`RunConfigStore`/`FeatureStore`/`ArtifactStore`/`ScoreStore` loga em stderr e devolve default/lista vazia — o padrão "fail-open" é consistente em todos os stores | `StateStore.cs:42-47`, `Trace.cs:96-101`, `FeatureStore.cs:125-129`, etc. |
| 6.8 | Manifesto de input ativo, classificação de proveniência, limites por fonte | **Não atende** | `DocsReader.Read` concatena **todo** `*.md`/`*.txt` de uma pasta sem manifesto explícito, sem digest, sem classe de confiança por arquivo | `DocsReader.cs:33-70` |
| 6.8 | Teto de bytes agregado com truncamento em fronteira UTF-8 registrado | **Parcial** | Há um teto de caracteres (`DocsMaxChars`) e truncamento com aviso em stderr, mas o corte é por `sb.Length` (UTF-16 chars, não octetos UTF-8) e não é registrado em evidência, só em log solto | `DocsReader.cs:60-66` |
| 6.8 | Skills allowlisted com digest correspondente à política | **Não atende** | `PromptFormatter.Skills`/`ReadSkills` inlina qualquer skill por nome/caminho, sem checagem de digest | `PromptFormatter.cs:8-16,46-76` |
| 6.9 | Capability broker medeia todo efeito nomeado (fs/net/git/process/secret) | **Não atende** | Chamadas diretas a `Process`/`GitCommand` a partir dos flows, sem broker central; `ARCHITECTURE.md` atribui essa responsabilidade à IDE, não ao harness | `GitCommand.cs`, `DevelopmentTasks.Verify.cs:42-93` |
| 6.9 | Rede negada por padrão | **N/A no engine, mas delegado sem controle** | O harness não faz chamadas de rede diretamente, mas também não impõe política de egress sobre o que o agente/IDE faz | — |
| 6.9 | Subprocessos com ambiente mínimo, sem credenciais desnecessárias | **Parcial** | `GitCommand`/`Process` de verify usam `ArgumentList` (evita shell injection) e têm timeout com `Kill(entireProcessTree: true)`, mas herdam o ambiente do processo pai por padrão (sem `EnvironmentVariables.Clear()`) | `GitCommand.cs:11-35`, `DevelopmentTasks.Verify.cs:44-93` |
| 6.10 | Classificação de efeitos A0–A3 com aprovação por classe | **Não atende** | Não existe essa classificação no código; commit (A2) e potencialmente push são tratados como efeitos automáticos | `DevelopmentTasks.Handoff.cs:71-74` |
| 6.10 | Auto-approval/bypass NÃO deve ser default de pacote | **Não atende** | `.claude/settings.json:3` → `"defaultMode": "bypassPermissions"`; `.vscode/settings.json:8` e `package.sh:353-366` geram `chat.tools.terminal.autoApprove` | — |
| 6.11 | Neutralização de hooks Git, pathspec derivado, scan de segredo antes do commit | **Não atende** | `GitCommand.Run` não seta `core.hooksPath`/`-c core.hooksPath=/dev/null`; `git add -A -- . :(exclude).harness` é praticamente repo-wide; nenhum scan de segredo | `GitCommand.cs:11-35`, `DevelopmentTasks.Handoff.cs:56-58` |
| 6.11 | Commit requer autorização A2 ou pré-autorização explícita | **Não atende** | Commit roda automaticamente ao fim de cada feature verificada, sem gate | `DevelopmentTasks.Handoff.cs:71-74` |
| 6.12 | Verificação não pode depender só de script editável pelo agente | **Não atende** | `verify-feature.sh` é o único gate de verificação e vive dentro do `targetDir`; o resultado alimenta diretamente `FeatureStore.MarkPassed` | `DevelopmentTasks.Verify.cs:15-40`, `DevelopmentTasks.Handoff.cs:22-26` |
| 6.12 | Veredito rotulado `self_attested` quando vem de script editável | **Não atende** | O resultado de `verify-feature.sh` é tratado como PASS/FAIL definitivo, sem rótulo de proveniência | `DevelopmentTasks.Verify.cs:34-39` |
| 6.13 | Stream de auditoria com hash-chain, digests de política/implementação, decisão de autorização | **Parcial (evidência existe, sem integridade)** | `Trace.jsonl`/`ScoreStore`/`ArtifactStore` produzem evidência estruturada e append-only por natureza de arquivo, mas sem hash do evento anterior, sem digest de política, sem decisão de capability (porque não há capability broker) | `Trace.cs:48-61`, `ScoreStore.cs:15-27` |
| 6.13 | Timestamps RFC 3339 UTC | **Atende** | `TraceEntry.Timestamp` usa `DateTimeOffset.UtcNow`, serializado por `System.Text.Json` em formato ISO 8601/RFC 3339 | `Trace.cs:53` |
| 6.14 | Telemetria de sessão opt-in, minimizada, redigida | **Fora de `src/dotnet`** | Os scripts de uso/custo citados no RFC (`scripts/*_usage.py`) não fazem parte de `src/dotnet` — não avaliados aqui, mas o gap apontado pelo RFC (§4.4, linha "Evidence can contain sensitive local transcripts") permanece válido para o produto como um todo |
| 6.15 | SBOM, scan de dependência, build assinado, CI protegido | **Não atende** | `find .github` só retorna `.github/prompts/`; não existe `.github/workflows`, nem script de SBOM/assinatura em `src/dotnet` | — |
| 6.16 | Semântica cross-engine unificada (octetos UTF-8, JSON canônico, timeout process-tree) | **Não atende** | `DocsReader`/`EnvelopeValidation` medem tamanho em chars .NET (`string.Length`), não em octetos UTF-8, exatamente o desvio que o RFC aponta no Apêndice B item 1 | `DocsReader.cs:60`, `EnvelopeValidation.cs:29-38` |

## 3. Registro de risco cruzado (RFC §5.3)

| Risco | Rating do RFC | Status em `src/dotnet` | Evidência |
|---|---|---|---|
| IAO-R01 — Prompt injection muda a hierarquia de instrução | Crítico | **Aberto** | `DocsReader` concatena todo `docs/*.md`/`*.txt` sem rótulo de confiança; `PromptFormatter` inlina skills sem digest |
| IAO-R02 — Path do agente escapa da raiz autorizada | Crítico | **Aberto** | `PathResolver`/`ResolveTargetDir` sem containment canônico |
| IAO-R03 — Verificador controlado pelo agente aprova falsamente | Crítico | **Aberto** | `verify-feature.sh` roda de dentro do `targetDir`, é o único gate |
| IAO-R04 — Tampering de estado/trace esconde run inseguro | Alto | **Aberto** | Sem hash-chain, sem assinatura, sem geração em `StateStore`/`Trace` |
| IAO-R05 — Efeitos auto-aprovados excedem autoridade pretendida | Alto | **Aberto** | `bypassPermissions`/`autoApprove` como default de pacote; commit sem gate |
| IAO-R06 — Relatórios de sessão vazam segredos/dados pessoais | Alto | **Fora de `src/dotnet`** | Scripts de report não fazem parte do engine .NET avaliado |
| IAO-R07 — Dependência/pacote comprometido entra em release | Alto | **Aberto** | Sem lockfile enforcement visível em CI, sem SBOM, sem CI (`.github/workflows` ausente) |
| IAO-R08 — Drivers concorrentes corrompem/bifurcam um run | Alto | **Aberto** | Sem lease; dois processos podem escrever `.harness/state.json` ao mesmo tempo sem lock |
| IAO-R09 — Diferenças de runtime quebram equivalência de protocolo/evidência | Moderado | **Aberto (confirmado)** | Medição de tamanho em `string.Length` (.NET) diverge de `len()` (Python) e `String::len()` (Rust) — exatamente o Apêndice B item 1 do RFC |
| IAO-R10 — Config do workspace desliga budgets/aumenta timeouts | Alto | **Parcialmente mitigado** | `MaxAllowedTimeoutMs` é um teto hard que `harness.json` não pode ultrapassar; mas `MaxSteps`/`MaxInstructionChars` **não** têm teto externo equivalente — o workspace pode aumentá-los livremente |
| IAO-R11 — Falha de persistência tratada como estado vazio válido | Alto | **Aberto (confirmado)** | Padrão fail-open confirmado em todos os stores (`StateStore`, `Trace`, `FeatureStore`, `RunConfigStore`, `ArtifactStore`, `ScoreStore`) |
| IAO-R12 — Testes funcionais verdes escondem ausência de testes de segurança adversariais | Alto | **Aberto** | A suíte de 35 arquivos de teste cobre comportamento funcional (budgets, timeout, resumo, dependência) muito bem, mas nenhum teste cobre os 25 cenários adversariais do §11.2 (path escape, replay, tampering de trace, hook malicioso, etc.) |

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
  raiz protegida fora do CWD do agente (`HarnessConfig.cs`).
- Reescrever `PathResolver` e `ResolveTargetDir` como um único componente de containment
  canônico: resolver symlinks, validar contra uma lista de raízes autorizadas, rejeitar
  `/`/home (`PathResolver.cs`, `DevelopmentTasks.Handoff.cs:88-92`).
- Adicionar `runId`/`messageId`/`step` assinado ao `Envelope` e um lease de escritor único
  sobre `.harness/state.json` (`Envelope.cs`, `StateStore.cs`).
- Reescrever `StateStore.Save`/`Trace.Append` para escrita atômica (arquivo temporário no
  mesmo filesystem + rename) com número de geração e hash do evento anterior
  (`StateStore.cs:50-61`, `Trace.cs:48-61`).
- Trocar o padrão fail-open por fail-closed: falha de persistência autoritativa deve produzir
  um estado `control_failure` explícito, não devolver default silenciosamente — afeta todos os
  `catch` genéricos em `StateStore`, `FeatureStore`, `RunConfigStore`, `ArtifactStore`,
  `ScoreStore`.

**Fase 2 — Capability broker e aprovação.**
- Introduzir um broker central que intercepta toda chamada a `GitCommand.Run` e ao `Process`
  de `DevelopmentTasks.Verify.cs`, classifica o efeito em A0–A3 e aplica a regra de aprovação
  correspondente antes de executar.
- Trocar `git add -A -- . :(exclude).harness` por pathspec derivado do change-set autorizado;
  adicionar `-c core.hooksPath=<vazio>` (ou equivalente) em todo `GitCommand.Run` que faça
  commit, para neutralizar hooks do repositório-alvo (`GitCommand.cs`, `DevelopmentTasks.Handoff.cs:56-74`).
- Remover `"defaultMode": "bypassPermissions"` de `.claude/settings.json` e o
  `chat.tools.terminal.autoApprove` gerado por `package.sh`/`.vscode/settings.json` como
  default de pacote; se unattended operation for desejado, exigir uma política assinada que
  enumere exatamente as capacidades pré-autorizadas (§6.10).

**Fase 3 — TEVV independente e proveniência de prompt.**
- Parar de tratar o exit code de `verify-feature.sh` como veredito autoritativo em
  `TryAutomatedVerify`/`CompleteVerifiedFeature`; rotular explicitamente como
  `self_attested` e introduzir um verificador que roda fora do `targetDir` controlado pelo
  agente, sobre uma cópia isolada da árvore (`DevelopmentTasks.Verify.cs`, `DevelopmentTasks.Handoff.cs:13-27`).
- Substituir a leitura indiscriminada de `docs/*.md`/`*.txt` em `DocsReader.Read` por um
  manifesto de input ativo explícito (path, digest, classe de confiança, byte count) e aplicar
  o mesmo tratamento a `PromptFormatter.ReadSkills` (digest de skill contra allowlist da
  política) (`DocsReader.cs`, `PromptFormatter.cs`).

**Fase 4 — Privacidade e supply chain.**
- Criar `.github/workflows` com build, teste, SAST/SCA/secret scan e geração de SBOM para o
  projeto .NET (hoje inexistente).
- Assinar os artefatos de release e publicar informação de verificação; pinar versão de SDK
  .NET/pacotes usados no build.

**Fase 5 — Conformidade Assured.**
- Unificar medição de tamanho para octetos UTF-8 em `DocsReader`/`EnvelopeValidation`
  (hoje usam `string.Length`/`sb.Length`, que são unidades UTF-16), fechando o Apêndice B
  item 1 do RFC.
- Construir a suíte de conformidade cross-engine (resume .NET↔Python↔Rust) e os 25 testes
  adversariais do §11.2 — nenhum existe hoje na suíte de 35 arquivos de teste.

## 6. Fora de escopo desta avaliação

- Os engines Python e Rust não foram lidos nem avaliados; o RFC é cross-engine e o pedido foi
  especificamente sobre `src/dotnet`.
- Não foi executado nenhum dos 25 testes adversariais do §11.2 (path escape, replay, tampering,
  hook malicioso, etc.) — esta é uma leitura estática de código contra o texto normativo, não
  uma auditoria de penetração ou um assessment formal nos termos do §11 (que exige exame,
  entrevista e teste).
- Os scripts de telemetria de uso/custo (`scripts/*_usage.py`) citados no RFC como fonte do
  risco IAO-R06 não fazem parte de `src/dotnet` e não foram avaliados aqui.
