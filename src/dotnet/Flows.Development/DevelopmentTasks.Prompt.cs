using Harness.Engine;
namespace Flows.Development;

/// <summary>
/// Construção dos prompts do flow de desenvolvimento — a "estratégia" separada da máquina de
/// estados em <c>DevelopmentTasks.cs</c>. Cada passo referencia o token de saída por constante
/// (<c>$XXX</c>): o mesmo nome que o driver preenche e devolve como arg do próximo envelope.
/// </summary>
public static partial class DevelopmentTasks
{
    // Tokens de saída (o driver guarda o artefato do passo nestes e os devolve como args).
    private const string FEATURES = "$FEATURES";
    private const string VERIFY_CMD = "$VERIFY_CMD";
    private const string TARGET_DIR = "$TARGET_DIR";
    private const string NOTE = "$NOTE";
    private const string SMOKE = "$SMOKE";
    private const string SUMMARY = "$SUMMARY";
    private const string RESULT = "$RESULT";
    private const string COMMIT = "$COMMIT";

    // Forma da feature_list em raw string SEM interpolação (chaves literais) — embutida nos
    // prompts via {FeaturesShape} para não colidir com a interpolação de $"""...""".
    private const string FeaturesShape =
        """[{"id":1,"title":"...","priority":1,"dependsOn":[]}, ...]""";

    // --- session 0: inicializador -----------------------------------------

    private static string InitializerPrompt(string content, string[] files) =>
        PromptFormatter.Format(
            input: $"""
            Você é o INICIALIZADOR (session 0). A partir do brief abaixo:
            1. Garanta um repositório Git no diretório-alvo (rode `git init` se necessário) e crie/reaproveite uma branch de trabalho dedicada (nunca direto em main/master).
            2. Escafolde o ambiente do projeto-alvo: crie um `init.sh` idempotente que instala dependências e sobe/builda o app, e a estrutura mínima de pastas.
            3. Expanda o brief numa lista PRIORIZADA de features pequenas e verificáveis, cada uma implementável e testável isoladamente. Numere a prioridade (1 = mais alta). Se uma feature só faz sentido depois de outra(s) (ex.: precisa de um schema que outra feature cria), registre os ids delas em `dependsOn` — array vazio quando não houver dependência. O harness respeita essa ordem além da prioridade.

            <brief fontes="{string.Join(", ", files)}">
            {content}
            </brief>

            Guarde em '{FEATURES}' um ARRAY JSON: {FeaturesShape}
            (só o array, sem passes — toda feature nasce pendente). Guarde o comando de
            verificação em '{VERIFY_CMD}' (ex.: `dotnet test`, `npm test`) e o diretório-alvo
            em '{TARGET_DIR}'.
            """,
            output: new Envelope(EnvelopeType.Command, "plan", [FEATURES, VERIFY_CMD, TARGET_DIR]),
            skills: PromptFormatter.Skills("dev-initializer"));

    private static string InitializerInteractive() =>
        PromptFormatter.Format(
            input: $"""
            Você é o INICIALIZADOR (session 0). Use a #tool:askQuestions e pergunte ao usuário:
            (a) o que construir (objetivo do app), (b) o diretório-alvo e (c) o comando de
            verificação (ex.: `dotnet test`, `npm test`). Depois:
            1. Garanta um repositório Git no diretório-alvo (rode `git init` se necessário) e crie/reaproveite uma branch de trabalho dedicada (nunca direto em main/master).
            2. Escafolde o ambiente: crie um `init.sh` idempotente no diretório-alvo.
            3. Expanda o objetivo numa lista PRIORIZADA de features pequenas e verificáveis. Se uma depender de outra, registre os ids em `dependsOn` (array vazio quando não houver).

            Guarde em '{FEATURES}' um ARRAY JSON {FeaturesShape},
            o comando em '{VERIFY_CMD}' e o diretório em '{TARGET_DIR}'.
            """,
            output: new Envelope(EnvelopeType.Command, "plan", [FEATURES, VERIFY_CMD, TARGET_DIR]),
            skills: PromptFormatter.Skills("dev-initializer"));

    private static string PlanRetryPrompt() =>
        PromptFormatter.Format(
            input: $"""
            Não consegui interpretar a lista de features. Reenvie em '{FEATURES}' um ARRAY JSON
            válido, exatamente no formato {FeaturesShape} — só o array, sem texto ao redor.
            Repita o comando `{VERIFY_CMD}` e `{TARGET_DIR}`.
            """,
            output: new Envelope(EnvelopeType.Command, "plan", [FEATURES, VERIFY_CMD, TARGET_DIR]));

    // --- loop por feature (uma sessão de contexto fresco) ------------------

    private static string BearingsPrompt() =>
        PromptFormatter.Format(
            input: $"""
            === NOVA SESSÃO (contexto limpo) ===
            Você é um agente de codificação começando uma sessão FRESCA. Não assuma nada da
            sessão anterior — todo o estado está nos artefatos persistentes.

            Oriente-se: rode `pwd`, leia o `progress.txt` e o `git log` recente para
            entender o que já foi feito. Resuma o que encontrou em '{NOTE}'.
            """,
            output: new Envelope(EnvelopeType.Command, "bearings", [NOTE]),
            skills: PromptFormatter.Skills("dev-bearings"));

    private static string SmokePrompt() =>
        PromptFormatter.Format(
            input: $"""
            Smoke test: rode `./init.sh` no diretório-alvo ({RunConfigStore.Load().TargetDir}) e confirme
            que o baseline sobe/builda sem erro antes de mexer em qualquer feature. Relate o
            resultado (ok ou o erro encontrado) em '{SMOKE}'.
            """,
            output: new Envelope(EnvelopeType.Command, "smoke", [SMOKE]),
            skills: PromptFormatter.Skills("dev-smoke"));

    private static string PickPrompt() =>
        PromptFormatter.Format(
            input: """
            Baseline confirmado. Envie o comando `pick` para receber a próxima feature a
            implementar (a de maior prioridade ainda pendente — o harness escolhe).
            """,
            output: new Envelope(EnvelopeType.Command, "pick", []));

    private static string ImplementPrompt(Feature feature) =>
        PromptFormatter.Format(
            input: $"""
            Implemente EXCLUSIVAMENTE esta feature, de forma incremental e mínima — nada além
            dela:

            Feature #{feature.Id} (prioridade {feature.Priority}): {feature.Title}

            Trabalhe no diretório-alvo ({RunConfigStore.Load().TargetDir}). Ao terminar, resuma o que
            implementou em '{SUMMARY}'.
            """,
            output: new Envelope(EnvelopeType.Command, "implement", [SUMMARY]),
            skills: PromptFormatter.Skills("dev-implement"));

    private static string VerifyPrompt() =>
        PromptFormatter.Format(
            input: $"""
            Self-verify a feature #{State("current_feature_id")} ({State("current_feature_title")})
            como um usuário faria: rode `{RunConfigStore.Load().VerifyCmd}` no diretório-alvo
            ({RunConfigStore.Load().TargetDir}) e confirme o comportamento ponta a ponta.

            Responda em '{RESULT}' começando com `PASS` (tudo verde) ou `FAIL: <motivo>`.
            """,
            output: new Envelope(EnvelopeType.Command, "verify", [RESULT]),
            skills: PromptFormatter.Skills("dev-verify"));

    private static string VerifyRetryPrompt() =>
        PromptFormatter.Format(
            input: $"""
            O veredito do self-verify não começou com `PASS` nem `FAIL`. Reexecute, se
            necessário, `{RunConfigStore.Load().VerifyCmd}` no diretório-alvo ({RunConfigStore.Load().TargetDir}) e
            responda em '{RESULT}' começando exatamente com `PASS` ou `FAIL: <motivo>`.
            """,
            output: new Envelope(EnvelopeType.Command, "verify", [RESULT]),
            skills: PromptFormatter.Skills("dev-verify"));

    private static string FixPrompt() =>
        PromptFormatter.Format(
            input: $"""
            A verificação FALHOU na feature #{State("current_feature_id")}
            ({State("current_feature_title")}). Corrija a implementação (ainda SÓ esta feature)
            e resuma o ajuste em '{SUMMARY}' — em seguida verificamos de novo.
            """,
            output: new Envelope(EnvelopeType.Command, "implement", [SUMMARY]),
            skills: PromptFormatter.Skills("dev-implement"));

    private static string HandoffPrompt() =>
        PromptFormatter.Format(
            input: $"""
            Deixe o estado LIMPO para a próxima sessão:
            1. `git commit` com mensagem descritiva referenciando a feature #{State("current_feature_id")}. Se o diretório-alvo não estiver em um repositório Git, registre isso explicitamente como `NO_GIT: <motivo>`.
            2. Anexe uma linha ao `progress.txt`: feature concluída, o que foi feito e como verificar.

            Confirme com o hash do commit ou `NO_GIT: <motivo>` em '{COMMIT}'.
            """,
            output: new Envelope(EnvelopeType.Command, "handoff", [COMMIT]),
            skills: PromptFormatter.Skills("dev-handoff"));

    private static string HandoffRetryPrompt() =>
        PromptFormatter.Format(
            input: $"""
            A confirmação do handoff veio vazia. Atualize `progress.txt` no diretório-alvo
            ({RunConfigStore.Load().TargetDir}) e responda em '{COMMIT}' com o hash do commit ou
            `NO_GIT: <motivo>` quando não houver repositório Git.
            """,
            output: new Envelope(EnvelopeType.Command, "handoff", [COMMIT]),
            skills: PromptFormatter.Skills("dev-handoff"));
}
