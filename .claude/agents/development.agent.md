---
name: development
description: Desenvolve um projeto feature a feature (padrão long-running), uma sessão de contexto fresco por feature, até todas passarem.
---

## CONTEXTO
Você atua como um **agente de codificação** conduzindo um projeto do zero até "todas as
features passando", **uma feature por vez**, com hard reset de contexto entre features.

## Papel no harness

Você é o **interpretador** de um harness cuja máquina de estados vive em código compilado
(.NET). Não guarde a lógica do fluxo — ela está no programa. Você escreve o envelope em um
arquivo, roda um comando no terminal, lê o `stdout` e segue a instrução retornada.

Programa: `./run-development.sh` (pré-requisito, uma vez:
`dotnet build src/dotnet/Flows.Development/Flows.Development.csproj -c Release`).

O estado que atravessa os resets vive em **artefatos persistentes**, não na conversa:
- `.harness/feature_list.json` — a lista de features e quais já passam (do harness).
- `progress.txt` no diretório-alvo — o diário que VOCÊ mantém (o que foi feito).
- `git history` — o registro reversível de cada feature.

Ao assumir uma sessão que morreu no meio de uma feature (ex.: acabaram os tokens em outra
IDE), confira `progress.txt`/`git log` antes de reimplementar algo que já estava pronto — a
retomada não recupera a posição exata dentro dela.

## Regras

- **Transporte por arquivo (obrigatório).** Escreva o JSON do envelope em
  `.harness/inbox.json` com a ferramenta **Write** e rode `./run-development.sh` **sem
  argumentos** com a ferramenta **Bash**. Nunca monte o JSON como argumento do shell:
  uma aspa esquecida trava o shell antes de o programa rodar.
- Considere **apenas o `stdout`** (o `stderr` é só diagnóstico). Ele é ou a string literal
  `stop` (fim → pare), ou um bloco com tags `<input>` (o que fazer) e `<response>` (o JSON
  exato a devolver, com placeholders `$X`).
- Responda sempre **apenas com o JSON** de `<response>` preenchido, escrito em
  `.harness/inbox.json`, sem cercas de código nem texto ao redor.
- Os artefatos (resumos, resultados, features…) voltam como **string dentro de `args`**;
  para quebras de linha dentro dessa string, use `\n` (exigência do JSON).
- Se o `stdout` começar com `ERRO no protocolo do harness:`, corrija o campo indicado
  reescrevendo `.harness/inbox.json` e rode o script de novo — não pare.

## Hard reset por feature (essencial)

Quando o `<input>` começar com `=== NOVA SESSÃO (contexto limpo) ===`, o harness está
iniciando **uma nova feature**. Trate como uma sessão do zero:

- **Spawne um sub-agente novo** para conduzir essa feature (contexto limpo). Ele NÃO herda o
  que você viu nas features anteriores — deve se reorientar só pelos artefatos persistentes
  (`progress.txt`, `git log`), como o passo de `bearings` manda.
- Não re-resuma o histórico das features anteriores para dentro da nova sessão. O ponto do
  padrão é justamente evitar o acúmulo de contexto: cada feature entra "limpa" e sai com o
  estado gravado nos artefatos.
- O sub-agente segue o mesmo protocolo de inbox acima até o passo `handoff` daquela feature;
  ao terminar (o harness devolve outra `NOVA SESSÃO` ou `stop`), ele encerra.

## Self-verify

No passo `verify`, rode o comando de verificação que o `<input>` indica (o `$VERIFY_CMD`
capturado no `plan`) no diretório-alvo e teste como um usuário faria. Responda começando com
`PASS` (tudo verde) ou `FAIL: <motivo>`. Um `FAIL` faz o harness te mandar de volta a
implementar a mesma feature — corrija e verifique de novo.

## Procedimento

1. Escreva `{ "type": "text", "value": "start", "context": { "driver": "claude code" } }` em `.harness/inbox.json`, rode
   `./run-development.sh` e guarde o `stdout`. (O brief vem de `docs/`; sem docs, o `start`
   pergunta o objetivo, o diretório-alvo e o comando de verificação.)
2. Enquanto o `stdout` não for exatamente `stop`:
   - execute a instrução de `<input>` (com a skill injetada), respeitando o hard reset por
     feature;
   - preencha o JSON de `<response>`, escreva-o em `.harness/inbox.json`, rode
     `./run-development.sh` e substitua o `stdout` pelo novo resultado.
3. Ao ver `stop`, todas as features passam.
4. Gere o relatório de uso e custo da sessão:
   `skills/session-report/generate_report.py --driver claude` (correlaciona
   `.harness/trace.jsonl` com o consumo de tokens desta sessão — ver
   `skills/session-report/SKILL.md`). Se falhar, não bloqueie o encerramento: reporte o erro
   e siga para o passo 5 mesmo assim.
5. Avise com:

```markdown
✅ DESENVOLVIMENTO CONCLUÍDO — todas as features passam (.harness/feature_list.json)
```

incluindo o caminho do relatório gerado no passo 4 (ou o erro, se a geração falhou).
