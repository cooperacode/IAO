package main

import (
	"fmt"
	"strings"

	engine "github.com/cooperacode/IAO/src/go/harnessengine"
)

// Output tokens (the driver stores the step's artifact in these and returns them as the
// next envelope's args).
const (
	tokenFeatures  = "$FEATURES"
	tokenVerifyCmd = "$VERIFY_CMD"
	tokenTargetDir = "$TARGET_DIR"
	tokenNote      = "$NOTE"
	tokenSmoke     = "$SMOKE"
	tokenSummary   = "$SUMMARY"
	tokenResult    = "$RESULT"
	tokenCommit    = "$COMMIT"
)

// featuresShape is the feature_list shape embedded verbatim in the prompts.
const featuresShape = `[{"id":1,"title":"...","priority":1,"dependsOn":[],"description":"...","references":[]}, ...]`

// featureContextBlock reinjects description/references (engine.Feature) into the implement
// prompt — the only point of the loop that receives the whole Feature object, not just
// title/id via StateStore. "" when the feature has neither (e.g. a feature_list.json from a
// version before these fields existed) — the block disappears, it doesn't show up empty.
func featureContextBlock(feature engine.Feature) string {
	if strings.TrimSpace(feature.Description) == "" && len(feature.References) == 0 {
		return ""
	}

	references := "nenhuma"
	if len(feature.References) > 0 {
		references = strings.Join(feature.References, ", ")
	}
	return fmt.Sprintf("Descrição: %s\nReferências do brief: %s\n\n", feature.Description, references)
}

// briefBlock reinjects the persisted brief (ArtifactStore, briefArtifactName) at the two
// points of the loop that actually reason about "what to build" — bearings and implement,
// and only there: smoke/pick/verify/fix/handoff just run a script or do bookkeeping, with
// no need for scope context. "" when the run started in interactive mode (no docs/) or is
// resuming a run from before this feature — in that case the block disappears, it doesn't
// stay empty.
func briefBlock() string {
	brief := engine.ReadArtifact(briefArtifactName)
	if strings.TrimSpace(brief) == "" {
		return ""
	}

	singleLine := strings.ReplaceAll(brief, "\r\n", "\\n")
	singleLine = strings.ReplaceAll(singleLine, "\n", "\\n")
	return fmt.Sprintf("<brief>%s</brief>", singleLine)
}

// --- session 0: initializer -----------------------------------------

func InitializerPrompt(content string, files []string) string {
	input := fmt.Sprintf(`Você é o INICIALIZADOR (session 0). A partir do brief abaixo:
1. Garanta um repositório Git no diretório-alvo (rode `+"`git init`"+` se necessário) e crie/reaproveite uma branch de trabalho dedicada (nunca direto em main/master).
2. Escafolde o ambiente do projeto-alvo: crie um `+"`init.sh`"+` idempotente que instala dependências e sobe/builda o app, um `+"`verify-feature.sh <id>`"+` idempotente que verifica uma feature, e a estrutura mínima de pastas.
3. Expanda o brief numa lista PRIORIZADA de features pequenas e verificáveis, cada uma implementável e testável isoladamente. Numere a prioridade (1 = mais alta). Se uma feature só faz sentido depois de outra(s) (ex.: precisa de um schema que outra feature cria), registre os ids delas em `+"`dependsOn`"+` — array vazio quando não houver dependência. O harness respeita essa ordem além da prioridade. Preencha também, para cada feature: `+"`description`"+`, uma descrição objetiva do que ela faz (até %d caracteres); e `+"`references`"+`, os códigos explícitos citados no brief que se relacionam a ela (ex.: "RF-003", "JIRA-142", uma seção nomeada) — array vazio se o brief não citar nenhum código explícito para essa feature (não invente um).

<brief fontes="%s">
%s
</brief>

Guarde em '%s' um ARRAY JSON: %s
(só o array, sem passes — toda feature nasce pendente). Guarde o comando de
verificação em '%s' (ex.: `+"`dotnet test`"+`, `+"`npm test`"+`) e o diretório-alvo
em '%s'. O `+"`verify-feature.sh`"+` pode rodar a suite completa no começo:
`+"`./init.sh`"+`, depois `+"`$VERIFY_CMD`"+`, imprimir `+"`PASS: feature <id> ...`"+` e sair 0.`,
		engine.DescriptionMaxChars, strings.Join(files, ", "), content, tokenFeatures, featuresShape, tokenVerifyCmd, tokenTargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "plan", []string{tokenFeatures, tokenVerifyCmd, tokenTargetDir}),
		engine.Skills("dev-initializer"))
}

func InitializerInteractive() string {
	input := fmt.Sprintf(`Você é o INICIALIZADOR (session 0). Use a #tool:askQuestions e pergunte ao usuário:
(a) o que construir (objetivo do app), (b) o diretório-alvo e (c) o comando de
verificação (ex.: `+"`dotnet test`"+`, `+"`npm test`"+`). Depois:
1. Garanta um repositório Git no diretório-alvo (rode `+"`git init`"+` se necessário) e crie/reaproveite uma branch de trabalho dedicada (nunca direto em main/master).
2. Escafolde o ambiente: crie um `+"`init.sh`"+` idempotente e um `+"`verify-feature.sh <id>`"+` idempotente no diretório-alvo.
3. Expanda o objetivo numa lista PRIORIZADA de features pequenas e verificáveis. Se uma depender de outra, registre os ids em `+"`dependsOn`"+` (array vazio quando não houver). Preencha também `+"`description`"+` (até %d caracteres) e `+"`references`"+` (códigos explícitos citados pelo usuário para essa feature; array vazio se não houver nenhum).

Guarde em '%s' um ARRAY JSON %s,
o comando em '%s' e o diretório em '%s'. O `+"`verify-feature.sh`"+` pode rodar a suite completa no começo:
`+"`./init.sh`"+`, depois `+"`$VERIFY_CMD`"+`, imprimir `+"`PASS: feature <id> ...`"+` e sair 0.`,
		engine.DescriptionMaxChars, tokenFeatures, featuresShape, tokenVerifyCmd, tokenTargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "plan", []string{tokenFeatures, tokenVerifyCmd, tokenTargetDir}),
		engine.Skills("dev-initializer"))
}

func PlanRetryPrompt() string {
	input := fmt.Sprintf(`Não consegui interpretar a lista de features. Reenvie em '%s' um ARRAY JSON
válido, exatamente no formato %s — só o array, sem texto ao redor.
Repita o comando '%s' e '%s'.`, tokenFeatures, featuresShape, tokenVerifyCmd, tokenTargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "plan", []string{tokenFeatures, tokenVerifyCmd, tokenTargetDir}),
		nil)
}

// --- per-feature loop (one fresh-context session) ------------------

func BearingsPrompt() string {
	input := fmt.Sprintf(`=== NOVA SESSÃO (contexto limpo) ===
Você é um agente de codificação começando uma sessão FRESCA. Não assuma nada da
sessão anterior — todo o estado está nos artefatos persistentes.
%s
Oriente-se com saída curta: rode `+"`pwd`"+`, leia só o fim do `+"`progress.txt`"+` e o
`+"`git log --oneline`"+` recente para entender o que já foi feito. Não cole logs
longos; se precisar preservar detalhe, salve em `+"`.harness/logs/`"+`.

Resuma o que encontrou em '%s' em 2-4 linhas.`, briefBlock(), tokenNote)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "bearings", []string{tokenNote}),
		engine.Skills("dev-bearings"))
}

func SmokePrompt() string {
	input := fmt.Sprintf("Smoke test: rode `./init.sh` no diretório-alvo (%s) e confirme\n"+
		"que o baseline sobe/builda sem erro antes de mexer em qualquer feature. Salve a\n"+
		"saída completa em `.harness/logs/smoke.log` e relate em '%s' só `ok` ou o\n"+
		"erro principal e o caminho do log.", engine.LoadRunConfig().TargetDir, tokenSmoke)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "smoke", []string{tokenSmoke}),
		engine.Skills("dev-smoke"))
}

func PickPrompt() string {
	input := "Baseline confirmado. Envie o comando `pick` para receber a próxima feature a\n" +
		"implementar (a de maior prioridade ainda pendente — o harness escolhe)."

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "pick", []string{}),
		nil)
}

func ImplementPrompt(feature engine.Feature) string {
	input := fmt.Sprintf("Implemente EXCLUSIVAMENTE esta feature, de forma incremental e mínima — nada além\n"+
		"dela:\n%s\nFeature #%d (prioridade %d): %s\n%sTrabalhe no diretório-alvo (%s). Se rodar comandos com\n"+
		"saída longa, salve em `.harness/logs/` e não cole logs no resumo. Ao terminar,\n"+
		"resuma o que implementou em '%s' em uma frase curta.",
		briefBlock(), feature.Id, feature.Priority, feature.Title, featureContextBlock(feature),
		engine.LoadRunConfig().TargetDir, tokenSummary)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "implement", []string{tokenSummary}),
		engine.Skills("dev-implement"))
}

func VerifyPrompt() string {
	config := engine.LoadRunConfig()
	input := fmt.Sprintf("O harness não encontrou `verify-feature.sh` no diretório-alvo, então faça o\n"+
		"self-verify manual da feature #%s\n(%s) como um usuário faria: rode\n"+
		"`%s` no diretório-alvo (%s) e\nconfirme o comportamento ponta a ponta. Salve a saída completa em\n"+
		"`.harness/logs/verify-%s.log`.\n\nResponda em '%s' começando com `PASS` ou `FAIL: <motivo>`, incluindo só o\n"+
		"erro principal e o caminho do log.",
		state(currentFeatureIdKey), state(currentFeatureTitleKey), config.VerifyCmd, config.TargetDir,
		state(currentFeatureIdKey), tokenResult)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "verify", []string{tokenResult}),
		engine.Skills("dev-verify"))
}

func VerifyRetryPrompt() string {
	config := engine.LoadRunConfig()
	input := fmt.Sprintf("O veredito do self-verify não começou com `PASS` nem `FAIL`. Reexecute, se\n"+
		"necessário, `%s` no diretório-alvo (%s)\nsalvando a saída completa em `.harness/logs/verify-%s.log`.\n"+
		"Responda em '%s' começando exatamente com `PASS` ou `FAIL: <motivo>`,\nsem colar logs longos.",
		config.VerifyCmd, config.TargetDir, state(currentFeatureIdKey), tokenResult)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "verify", []string{tokenResult}),
		engine.Skills("dev-verify"))
}

func FixPrompt(verifyFailure string) string {
	failure := ""
	if strings.TrimSpace(verifyFailure) != "" {
		failure = fmt.Sprintf("Falha observada: %s\n\n", verifyFailure)
	}

	input := fmt.Sprintf("A verificação FALHOU na feature #%s\n(%s). %sCorrija a implementação (ainda SÓ esta feature).\n"+
		"Se consultar logs, leia só o trecho relevante. Resuma o ajuste em '%s' —\nem seguida verificamos de novo.",
		state(currentFeatureIdKey), state(currentFeatureTitleKey), failure, tokenSummary)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "implement", []string{tokenSummary}),
		engine.Skills("dev-implement"))
}

func HandoffPrompt(automaticFailure string) string {
	failure := ""
	if strings.TrimSpace(automaticFailure) != "" {
		failure = fmt.Sprintf("O handoff automatico falhou: %s\n\n", automaticFailure)
	}

	input := fmt.Sprintf("%sDeixe o estado LIMPO para a próxima sessão:\n"+
		"1. `git commit` com mensagem descritiva referenciando a feature #%s. Se o diretório-alvo não estiver em um repositório Git, registre isso explicitamente como `NO_GIT: <motivo>`.\n"+
		"2. Anexe uma linha ao `progress.txt`: feature concluída, o que foi feito e como verificar.\n\n"+
		"Confirme com o hash do commit ou `NO_GIT: <motivo>` em '%s'.",
		failure, state(currentFeatureIdKey), tokenCommit)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "handoff", []string{tokenCommit}),
		engine.Skills("dev-handoff"))
}

func HandoffRetryPrompt() string {
	input := fmt.Sprintf("A confirmação do handoff veio vazia. Atualize `progress.txt` no diretório-alvo\n"+
		"(%s) e responda em '%s' com o hash do commit ou\n`NO_GIT: <motivo>` quando não houver repositório Git.",
		engine.LoadRunConfig().TargetDir, tokenCommit)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "handoff", []string{tokenCommit}),
		engine.Skills("dev-handoff"))
}
