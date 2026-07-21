#!/usr/bin/env bash
# Empacota uma versão NATIVA (Native AOT) do fluxo de desenvolvimento num pacote
# autocontido, escolhendo o sistema operacional (RID) e a IDE. O pacote inclui o binário
# nativo, as skills em runtime e o adaptador de IDE correspondente para o fluxo de
# desenvolvimento. Gera:
#
#   dist/flows-<rid>-v<version>/
#     bin/Flows.Development           # binário nativo do fluxo de desenvolvimento
#     skills/                         # skills injetadas em runtime
#     run-development.sh (+ .cmd win) # wrapper do desenvolvimento → ./bin
#     <adaptador da IDE no caminho esperado>
#     <config de aprovação da IDE>    # executa o wrapper sem prompt por comando
#     START-HERE.md                   # como rodar o fluxo na IDE escolhida
#
# Uso:
#   ./package.sh --os <rid> --ide <claude|copilot|devin|codex> [--version <v>]
#   ./package.sh                     # modo interativo (menus)
#
# RIDs: osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

RIDS=(osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64)
IDES=(claude copilot devin codex)
# Neste branch o projeto empacotado é apenas o fluxo de desenvolvimento.
FLOWS=(development)

RID=""
IDE=""
VERSION="1.0.0"

usage() {
  echo "uso: ./package.sh --os <rid> --ide <claude|copilot|devin|codex> [--version <v>]"
  echo "RIDs: ${RIDS[*]}"
}

# ---- parse de argumentos ----
while [[ $# -gt 0 ]]; do
  case "$1" in
    --os|--rid) RID="${2:-}"; shift 2;;
    --ide)      IDE="${2:-}"; shift 2;;
    --version|-v) VERSION="${2:-}"; shift 2;;
    -h|--help)  usage; exit 0;;
    *) echo "argumento desconhecido: $1" >&2; usage; exit 1;;
  esac
done

contains() { local x; for x in "${@:2}"; do [[ "$x" == "$1" ]] && return 0; done; return 1; }

# ---- metadados por fluxo ----
project_for() { case "$1" in
  development) echo "src/dotnet/Flows.Development/Flows.Development.csproj";;
esac; }
assembly_for() { case "$1" in
  development) echo "Flows.Development";;
esac; }
wrapper_for() { case "$1" in
  development) echo "run-development.sh";;
esac; }
# adaptador por IDE+fluxo → "SRC<TAB>REL" (REL = caminho esperado pela IDE dentro do pacote)
adapter_for() { case "$1:$2" in
  claude:development)  printf '%s\t%s\n' ".claude/agents/development.agent.md"    ".claude/agents/development.agent.md";;
  copilot:development) printf '%s\t%s\n' ".github/prompts/development.prompt.md"  ".github/prompts/development.prompt.md";;
  devin:development)   printf '%s\t%s\n' ".devin/workflows/development.md"        ".devin/workflows/development.md";;
  codex:development)   printf '%s\t%s\n' ".codex/agents/development.toml"         ".codex/agents/development.toml";;
esac; }

# ---- seleção interativa quando faltar ----
if [[ -z "$RID" ]]; then
  echo "Selecione o sistema operacional (RID):"
  select r in "${RIDS[@]}"; do [[ -n "${r:-}" ]] && RID="$r" && break; done
fi
if [[ -z "$IDE" ]]; then
  echo "Selecione a IDE:"
  select i in "${IDES[@]}"; do [[ -n "${i:-}" ]] && IDE="$i" && break; done
fi

# ---- validação ----
contains "$RID" "${RIDS[@]}" || { echo "RID inválido: '$RID' (use: ${RIDS[*]})" >&2; exit 1; }
contains "$IDE" "${IDES[@]}" || { echo "IDE inválida: '$IDE' (use: ${IDES[*]})" >&2; exit 1; }
[[ -n "$VERSION" ]] || { echo "versão vazia" >&2; exit 1; }

# adaptador do fluxo precisa existir para a IDE escolhida
for flow in "${FLOWS[@]}"; do
  IFS=$'\t' read -r src _rel < <(adapter_for "$IDE" "$flow")
  [[ -f "$src" ]] || { echo "adaptador não encontrado: $src (ide=$IDE, fluxo=$flow)" >&2; exit 1; }
done

# ---- aviso: AOT não faz cross-compile entre SOs ----
HOST_OS="$(uname -s)"
TARGET_OS="desconhecido"
case "$RID" in osx-*) TARGET_OS="Darwin";; linux-*) TARGET_OS="Linux";; win-*) TARGET_OS="Windows";; esac
if [[ "$TARGET_OS" != "desconhecido" && "$HOST_OS" != "$TARGET_OS" ]]; then
  echo "[aviso] Native AOT compila para o SO do host ($HOST_OS)." >&2
  echo "[aviso] Alvo '$RID' é $TARGET_OS — rode este script nesse SO (ou num CI) se o publish falhar." >&2
fi

WINEXT=""; [[ "$RID" == win-* ]] && WINEXT=".exe"
OUT="dist/flows-$RID-v$VERSION"

echo "[package] montando $OUT …"
rm -rf "$OUT"
mkdir -p "$OUT/bin"
cp -R skills "$OUT/skills"
cp harness.json "$OUT/harness.json"   # config das variáveis do harness (tetos, docs)
cp AGENTS.md "$OUT/AGENTS.md"         # instruções de projeto para Codex e outros agentes que leem AGENTS.md

# ---- por fluxo: publish AOT, binário, wrapper(s) e adaptador ----
for flow in "${FLOWS[@]}"; do
  project="$(project_for "$flow")"
  assembly="$(assembly_for "$flow")"
  wrapper="$(wrapper_for "$flow")"
  bin="$assembly$WINEXT"

  echo "[package] publicando Native AOT — $flow ($RID)…"
  dotnet publish "$project" -c Release -r "$RID" -p:PublishAot=true

  pubdir="$(dirname "$project")/bin/Release/net10.0/$RID/publish"
  [[ -f "$pubdir/$bin" ]] || { echo "[erro] binário não encontrado em $pubdir/$bin" >&2; exit 1; }
  cp "$pubdir/$bin" "$OUT/bin/"

  # wrapper .sh
  cat > "$OUT/$wrapper" <<EOF
#!/usr/bin/env bash
# Inicia um passo do fluxo '$flow'. Rode a partir desta pasta (o estado e as skills
# são relativos a ela). Ex.: ./$wrapper '{ "type": "text", "value": "start" }'
set -euo pipefail
DIR="\$(cd "\$(dirname "\${BASH_SOURCE[0]}")" && pwd)"
cd "\$DIR"
exec "./bin/$bin" "\$@"
EOF
  chmod +x "$OUT/$wrapper"

  # wrapper .cmd (Windows)
  if [[ "$RID" == win-* ]]; then
    cmd="${wrapper%.sh}.cmd"
    cat > "$OUT/$cmd" <<EOF
@echo off
cd /d "%~dp0"
"bin\\$bin" %*
EOF
  fi

  # adaptador da IDE (no caminho que ela espera)
  IFS=$'\t' read -r src rel < <(adapter_for "$IDE" "$flow")
  mkdir -p "$OUT/$(dirname "$rel")"
  cp "$src" "$OUT/$rel"
done

# ---- config de aprovação da IDE (wrappers rodam sem prompt por comando) ----
CONFROW=""
case "$IDE" in
  claude)
    # allowlist de permissões: o agente dirige os wrappers sem pedir aprovação a cada passo
    mkdir -p "$OUT/.claude"
    cat > "$OUT/.claude/settings.json" <<'EOF'
{
  "permissions": {
    "allow": [
      "Bash(./run-development.sh *)",
      "Bash(./run-development.cmd *)",
      "Bash(chmod +x *)"
    ]
  }
}
EOF
    CONFROW="| \`.claude/settings.json\` | allowlist: wrappers executam sem prompt de aprovação |
"
    ;;
  copilot)
    # auto-approve do terminal no agent mode (o VS Code pede uma confirmação única
    # para honrar auto-approve vindo de settings de workspace)
    mkdir -p "$OUT/.vscode"
    cat > "$OUT/.vscode/settings.json" <<'EOF'
{
  "chat.tools.terminal.autoApprove": {
    "/^\.\\/(run-development)\\.sh\\b/": true,
    "/^(\\.\\\\)?(run-development)\\.cmd\\b/": true,
    "/^bash +run-development\\.sh\\b/": true,
    "/^chmod \\+x /": true
  }
}
EOF
    CONFROW="| \`.vscode/settings.json\` | auto-approve do terminal: wrappers executam sem prompt |
"
    ;;
  devin)
    # nada a gerar: os workflows copiados já trazem auto_execution_mode: 3 (auto-exec)
    ;;
  codex)
    # o Codex não lê config de aprovação do workspace; a instrução vai no START-HERE
    ;;
esac

# ---- instruções de início por IDE ----
DEV_REL="$(adapter_for "$IDE" development | cut -f2)"
case "$IDE" in
  claude)  START="1. Abra **esta pasta** no Claude Code.
2. **Desenvolvimento:** \`/agents\` → **development** e peça *\"Desenvolva: <objetivo do projeto>\"*. O agente dirige \`./run-development.sh\`, uma feature por vez, até todas passarem.";;
  copilot) START="1. Abra **esta pasta** no VS Code com GitHub Copilot em **agent mode**.
2. **Desenvolvimento:** selecione o prompt file **development** (\`.github/prompts/development.prompt.md\`) e peça *\"Desenvolva: <objetivo do projeto>\"*. O agente dirige \`./run-development.sh\`, uma feature por vez, até todas passarem.";;
  devin)   START="1. Abra **esta pasta** como workspace no Devin Desktop (os workflows já estão em \`.devin/workflows/\`).
2. **Desenvolvimento:** invoque \`/development\` e peça *\"Desenvolva: <objetivo do projeto>\"*. O Devin dirige \`./run-development.sh\`, uma feature por vez, até todas passarem.";;
  codex)   START="1. Abra **esta pasta** no Codex. Para o wrapper rodar sem aprovação por comando, inicie com \`codex --ask-for-approval never --sandbox workspace-write\` (o Codex não lê config de aprovação do workspace).
2. **Desenvolvimento:** peça *\"Use o agente customizado development para desenvolver: <objetivo do projeto>\"*. O agente em \`.codex/agents/development.toml\` dirige \`./run-development.sh\`, uma feature por vez, até todas passarem.";;
esac

WINROW=""
if [[ "$RID" == win-* ]]; then
  WINROW="| \`run-development.cmd\` | wrapper de execução no Windows |
"
fi

cat > "$OUT/START-HERE.md" <<EOF
# Fluxos — pacote nativo ($RID · v$VERSION · IDE: $IDE)

Pacote autocontido com o fluxo de desenvolvimento em binário nativo (sem runtime .NET),
mais as skills e o adaptador da IDE correspondente. O desenvolvimento constrói o projeto
feature a feature e salva snapshots em \`last-development.*\` para não colidir com outros
fluxos do workspace.

## Começar

$START

## Teste rápido (sem IDE)

\`\`\`bash
./run-development.sh '{ "type": "text", "value": "start" }'
\`\`\`
O binário deve imprimir um bloco \`<input>\`/\`<response>\` no stdout (ou \`stop\`).

## Conteúdo

| Caminho | O quê |
|---|---|
| \`bin/Flows.Development$WINEXT\` | binário nativo do fluxo de desenvolvimento |
| \`skills/\` | skills injetadas em runtime |
| \`harness.json\` | config do harness: tetos de passos/custo/tempo e pasta de docs |
| \`run-development.sh\` | wrapper de execução |
$WINROW| \`$DEV_REL\` | adaptador de desenvolvimento da IDE escolhida |
$CONFROW
EOF

echo "[package] pronto ✓  → $OUT"
echo "[package] conteúdo:"
find "$OUT" -type f | sed "s|^$OUT/|  |" | sort
