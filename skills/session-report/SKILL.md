---
name: session-report
description: "gerar relatorio HTML de custo/uso da sessao mais recente do harness (tokens, custo por passo, por comando, erros) para claude, codex ou copilot"
---

# SKILL: relatorio de uso e custo da sessao

Gera um relatorio HTML autocontido que correlaciona os passos do harness
(`.harness/trace.jsonl`) com o consumo real de tokens de um driver (IDE/agente) — sem isso,
"quanto custou essa execução, passo a passo" fica preso em linhas de tabela no terminal
(`scripts/harness_cost_correlate.py`), sem visão consolidada.

## Rodar (caminho do agente)

O driver é `skills/session-report/generate_report.py`. Ele encadeia dois scripts já
existentes, não reimplementa nada:

1. `scripts/<driver>_usage.py --json` — descobre a sessão mais recente (maior `last_ts`)
   daquele driver para este repo.
2. `scripts/harness_cost_correlate.py --usage-source <driver> --session <id> --trace-file .harness/trace.jsonl --json`
   — correlaciona os passos do trace com o consumo daquela sessão.
3. Normaliza e renderiza o HTML.

```bash
skills/session-report/generate_report.py --driver claude
skills/session-report/generate_report.py --driver codex
skills/session-report/generate_report.py --driver copilot
```

Gera `report/session-report-<driver>-<timestamp>.html` (pasta criada se não existir) e
imprime o caminho no final. **Confirmado nesta sessão**, rodando os três drivers contra os
dados reais deste repo: usando a sessão que efetivamente gerou `.harness/trace.jsonl`
(`--session daba97f0-b838-4b05-92f3-1b778de86d78`), o relatório reproduziu exatamente os
números do `.harness/report_custo_claude.txt` já existente no repo — 57 passos, $10.55
atribuídos, $11.46 total, 42m 40s de duração.

### Escopo (opcional)

```bash
skills/session-report/generate_report.py --driver claude --session <session-id>
skills/session-report/generate_report.py --driver codex --trace-file .harness/last-development.trace.jsonl
skills/session-report/generate_report.py --driver claude --out-dir /tmp/relatorios
```

`--session` pula a auto-detecção. `--trace-file` aponta para outro trace (default:
`.harness/trace.jsonl`; o repo também mantém `.harness/last-development.trace.jsonl`, um
snapshot idêntico da última execução).

## Pré-requisitos

- Python 3 (testado com 3.12) — sem dependências externas, só stdlib.
- `scripts/claude_usage.py`, `scripts/codex_usage.py`, `scripts/copilot_usage.py` e
  `scripts/harness_cost_correlate.py` já existentes na raiz do repo.
- Um `.harness/trace.jsonl` (ou outro passado via `--trace-file`) de uma execução do harness
  já feita — sem trace não há passo para correlacionar.

## O que o relatorio mostra

- **KPIs**: passos, erros, custo atribuído (soma dos passos), custo total da sessão (inclui
  consumo pós-último-passo, "não atribuído"), tokens totais, custo médio/passo.
- **Custo por comando** — gráfico de barras horizontal + tabela (cores atribuídas
  dinamicamente aos comandos vistos no trace, não fixas).
- **Custo por passo ao longo da execução** — gráfico de linha; passos com `outcome: error`
  aparecem com anel vermelho.
- **Erros registrados** — lista dos passos com `outcome: error` (oculta se não houver nenhum).
- **Tokens e custo por modelo** — agregado dentro da janela da sessão (passos + não
  atribuído).
- **Log completo** — tabela colapsável com todos os passos, na ordem original.
- **Avisos** — warnings dos scripts de usage/correlate, modelos sem preço cadastrado, e a nota
  de custo do Copilot (fatura por premium request, não por token — sem `$` estimado).

O layout (paleta, KPI grid, gráficos SVG, tooltip) segue o design system de
`curso/material/relatorio-execucao-harness.html` — este relatório reproduz a seção de
custo/execução daquele template (é gerado a partir da mesma fonte de dados,
`harness_cost_correlate.py`), mas fica de fora as seções de features/complexidade de código,
que dependem de `feature_list.json` e análise Roslyn — fora do escopo desta skill.

## Gotchas

- **A correlação é por janela de tempo, não por chave compartilhada** — igual ao
  `harness_cost_correlate.py` original: se a sessão auto-detectada (maior `last_ts`) for
  *posterior* ao trace (ex.: você já terminou o harness ontem e hoje abriu uma sessão nova só
  pra gerar o relatório), todo o consumo dessa sessão cai em "não atribuído" e cada passo
  aparece com custo `$0.00`. **Confirmado nesta sessão**: rodar sem `--session` pegou a sessão
  de hoje (que não tem overlap com o trace de ontem) e todos os 57 passos saíram zerados, com
  os 7M tokens jogados em "não atribuído". Para um relatório útil de uma execução passada,
  passe `--session <id-da-sessao-que-rodou-o-harness>` explicitamente.
- **Sem trace, sem relatório**: `harness_cost_correlate.py` exige um `--trace-file` existente
  — não há fallback para "resumo geral sem passos". Se `.harness/trace.jsonl` não existir
  (nenhuma execução do harness ainda), o driver falha com uma mensagem apontando para rodar o
  harness primeiro.
- **Custo do Copilot é sempre `n/d`** — o backend do `harness_cost_correlate.py` para copilot
  retorna `cost=None` sempre (fatura por premium request com multiplicador, não por token);
  o relatório mostra tokens normalmente mas todo KPI/coluna de custo vira "n/d", com uma nota
  explicativa no rodapé.
- **`--out-dir`/`--trace-file` default são sempre relativos à raiz do repo**, não ao
  diretório onde o comando foi chamado — `generate_report.py` resolve `REPO_ROOT` a partir do
  próprio caminho do arquivo (`skills/session-report/generate_report.py` → sobe dois níveis).

## Troubleshooting

- `Trace do harness nao encontrado: .harness/trace.jsonl` — rode uma execução do harness
  (`dev-initializer` + ciclo de features) antes, ou aponte `--trace-file` para um trace
  existente (ex.: `.harness/last-development.trace.jsonl`).
- `Nenhuma sessao de <driver> encontrada para este repo` — o driver escolhido nunca foi usado
  neste repositório (o `<driver>_usage.py` correspondente não achou sessões). Rode o script
  de usage direto (`python3 scripts/claude_usage.py`) para confirmar.
- `Erro ao rodar <script>.py (exit 1)` — o script subjacente (usage ou correlate) falhou; o
  stderr dele é repassado prefixado com `[<script>]` antes da mensagem de erro final.
