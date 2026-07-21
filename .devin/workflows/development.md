---
description: Desenvolve um projeto feature a feature (padrão long-running) — inicializador expande o brief em features priorizadas; um loop de sessões de contexto fresco implementa uma por vez (bearings, smoke, pick, implement, verify, handoff) até todas passarem — dirigindo o harness .NET Flows.Development.
auto_execution_mode: 3
---

# Desenvolvimento long-running

Leva um projeto do zero até "todas as features passando", **uma feature por vez**, com hard
reset de contexto entre features.

Você é o **interpretador** de um harness cuja máquina de estados vive em código compilado (.NET). **A lógica do fluxo está no programa, não com você.** A cada passo você escreve o envelope em um arquivo, roda um comando, lê o `stdout` e segue exatamente a instrução retornada. Não decida por conta própria qual é o próximo passo.

Programa: `./run-development.sh`

## Contrato do canal

- **`stdout`** = a próxima instrução. É a string literal `stop` (fim → pare), ou um bloco com `<input>` (o que fazer) e `<response>` (o JSON exato a devolver, com placeholders `$X`, e possivelmente um `<skills>` com a skill do passo).
- **`stderr`** = diagnóstico. Ignore ao decidir o próximo passo.
- **Sua saída de cada passo** = apenas o JSON de `<response>` preenchido, escrito em `.harness/inbox.json`, sem cercas de código nem texto ao redor.

## Regras

1. **Transporte por arquivo (obrigatório).** Escreva o JSON do envelope em `.harness/inbox.json` e rode `./run-development.sh` **sem argumentos**. Nunca monte o JSON como argumento do shell: uma aspa esquecida trava o shell antes de o programa rodar.
2. Baseie a decisão **apenas no `stdout`**.
3. Os artefatos (resumos, resultados, features…) voltam como **string dentro de `args`**; para quebras de linha, use `\n` (exigência do JSON).
4. Se o `stdout` começar com `ERRO no protocolo do harness:`, corrija o campo apontado reescrevendo `.harness/inbox.json` e **rode o script de novo** — não pare.

## Hard reset por feature (essencial)

Quando o `<input>` começar com `=== NOVA SESSÃO (contexto limpo) ===`, o harness inicia **uma nova feature**. Trate como sessão do zero: **spawne um sub-agente novo** para conduzi-la, que se reorienta **só** pelos artefatos persistentes (`progress.txt`, `git log`) — não herde nem re-resuma o contexto das features anteriores.

Ao assumir uma sessão que morreu no meio de uma feature (ex.: acabaram os tokens em outra
IDE), confira `progress.txt`/`git log` antes de reimplementar algo que já estava pronto — a
retomada não recupera a posição exata dentro dela.

## Self-verify

No passo `verify`, rode o comando de verificação indicado no `<input>` (`$VERIFY_CMD` capturado no `plan`) no diretório-alvo e teste como um usuário faria. Responda começando com `PASS` ou `FAIL: <motivo>`. Um `FAIL` faz o harness te mandar de volta a implementar a mesma feature.

## Passos

1. Garanta o build do harness (uma vez):
   ```bash
   dotnet build src/Flows.Development/Flows.Development.csproj -c Release
   ```
   (Ou binário nativo sem runtime .NET: `dotnet publish src/Flows.Development/Flows.Development.csproj -c Release -r linux-x64`.)

2. Inicie o fluxo: escreva `{ "type": "text", "value": "start", "context": { "driver": "devin" } }` em `.harness/inbox.json`, rode o script sem argumentos e guarde o `stdout`:
   ```bash
   ./run-development.sh
   ```

3. Enquanto o `stdout` **não** for exatamente `stop`:
   - Execute a instrução do bloco `<input>` (com a skill do bloco `<skills>`), respeitando o hard reset por feature.
   - Preencha o JSON do bloco `<response>`, escreva-o em `.harness/inbox.json` e rode `./run-development.sh` (sem argumentos).
   - Substitua o `stdout` pelo novo resultado e repita.

4. Ao ver `stop`, todas as features passam (`.harness/feature_list.json`). Reporte:

```markdown
✅ DESENVOLVIMENTO CONCLUÍDO — todas as features passam (.harness/feature_list.json)
```

> Sem relatório de uso/custo para Devin: `skills/session-report/` depende de um script
> `scripts/devin_usage.py` (equivalente a `claude_usage.py`/`codex_usage.py`/
> `copilot_usage.py`) que ainda não existe — não pule esta nota achando que é só chamar a
> skill com `--driver devin`, ela vai falhar (`choices` só aceita `claude`/`codex`/
> `copilot`). Não gere o relatório neste workflow até esse script existir.
