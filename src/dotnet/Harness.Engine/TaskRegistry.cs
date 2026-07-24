namespace Harness.Engine;

/// <summary>Dispatch domain-agnostic: parse do envelope, guarda de iteração e erro tipado.</summary>
public static class TaskRegistry
{
    // Teto de passos: impede loop infinito que queimaria tokens indefinidamente.
    // Valor vem do harness.json (ou do default) — ver HarnessConfig.
    public static int MaxSteps => HarnessConfig.Current.MaxSteps;

    public static string Dispatch(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, Func<Envelope?, string>> actions,
        IReadOnlyDictionary<string, Func<Envelope, ValidationResult>>? validators = null,
        int? maxSteps = null,
        Func<bool>? shouldResetOnStart = null)
    {
        // Argv presente → transporte clássico (retrocompatível). Argv vazio → lê o envelope
        // da inbox em arquivo, o transporte que elimina o hang de aspas do shell (ver Inbox).
        var fromInbox = args.Count == 0;
        var arg0 = args.Count >= 1 ? args[0] : Inbox.Read();

        var envelope = string.IsNullOrWhiteSpace(arg0)
            ? null
            : Envelope.Parse(arg0);

        // Só consome a inbox quando o parse deu certo — um JSON quebrado deve gerar o ERRO
        // corretivo e permanecer disponível para inspeção, não sumir silenciosamente.
        if (fromInbox && envelope is not null)
            Inbox.Consume();

        if (envelope is not null && envelope.Value == "start")
        {
            // Novo workflow começa do zero — estado e trace são truncados juntos. Mas um
            // "start" também chega quando uma sessão fresca (ex.: hard reset por feature do
            // Development) reabre um run em andamento — nesse caso é RETOMADA, não início, e
            // truncar aqui apagaria o trace/step acumulados de features anteriores. O flow
            // decide via shouldResetOnStart (ele sabe se há trabalho pendente); sem predicado,
            // o padrão é sempre resetar (retrocompatível com flows single-shot).
            if (shouldResetOnStart?.Invoke() ?? true)
            {
                StateStore.Reset();
                Trace.Reset();
            }

            // Contexto do driver (ex.: {"driver":"claude code"}) nasce aqui e sobrevive no
            // StateStore — PromptFormatter o reinjeta em toda saída até o próximo "start".
            // Independe do reset acima: mesmo numa retomada, o driver atual deve prevalecer.
            if (envelope.Context is { Count: > 0 } context)
                StateStore.SetContext(context);
        }

        // Guarda de iteração — hard stop sob a restrição de tokens do time.
        var step = StateStore.Increment();

        var costChars = StateStore.Load().CostChars;
        var command = envelope?.Value is { Length: > 0 } value ? value : "(unparsed)";

        var (result, outcome) = Resolve(envelope, step, costChars, actions, validators, maxSteps);

        // Uma linha por volta do loop: alimenta a Telemetria e o Evaluator de trajetória.
        Trace.Append(step, command, outcome, result.Length);

        // O custo da instrução emitida agora só é conhecido aqui — entra no acumulado
        // que o guard do próximo turno vai checar.
        StateStore.AddCost(result.Length);
        return result;
    }

    private static (string Result, string Outcome) Resolve(
        Envelope? envelope, int step, int costChars,
        IReadOnlyDictionary<string, Func<Envelope?, string>> actions,
        IReadOnlyDictionary<string, Func<Envelope, ValidationResult>>? validators,
        int? maxSteps = null)
    {
        // Teto de passos efetivo: o override por invocação (ex.: um flow long-running como o
        // Development, que precisa de mais folga) tem precedência sobre o global do harness.json.
        // Sem override, vale o do config — o Refinement/Evaluation seguem inalterados.
        var effectiveMaxSteps = maxSteps ?? MaxSteps;
        if (step > effectiveMaxSteps)
        {
            Console.Error.WriteLine($"[harness] limite de {effectiveMaxSteps} passos atingido; encerrando.");
            return ("stop", TraceOutcome.Budget);
        }

        // Teto de custo, segundo guard além do de passos. Chars de instrução emitida são
        // a única medida: é o que a engine atesta sozinha. Token real vive nos metadados
        // de billing do caller — um driver-LLM não tem como reportá-lo honestamente.
        var config = HarnessConfig.Current;
        if (config.MaxInstructionChars > 0 && costChars > config.MaxInstructionChars)
        {
            Console.Error.WriteLine(
                $"[harness] limite de {config.MaxInstructionChars} chars de instrução atingido ({costChars}); encerrando.");
            return ("stop", TraceOutcome.Budget);
        }

        // Erro tipado em vez de "stop" silencioso: o modelo recebe a causa e pode
        // reenviar o comando correto (loop corretivo, não término mudo).
        if (envelope is null)
            return (ErrorInstruction("Não foi possível interpretar o JSON recebido.", actions), TraceOutcome.Error);

        if (!actions.TryGetValue(envelope.Value, out var action))
            return (ErrorInstruction($"O comando '{envelope.Value}' não existe.", actions), TraceOutcome.Error);

        // Validação contextual: o comando existe, mas o VALOR atende à expectativa da task?
        // Falhou → mesmo caminho de erro corretivo dos casos acima; o driver corrige e reenvia.
        if (validators is not null
            && validators.TryGetValue(envelope.Value, out var validator)
            && validator(envelope) is { Ok: false } rejected)
        {
            return (ErrorInstruction(
                $"O comando '{envelope.Value}' foi recusado: {rejected.Reason} "
                + "Corrija o conteúdo de 'args' e reenvie o mesmo comando.", actions), TraceOutcome.Error);
        }

        // Guarda de tempo: uma task travada (loop infinito na lógica de domínio) prenderia
        // o processo indefinidamente. RunWithTimeout impõe o teto por passo; o estouro vira
        // erro tipado, capturado aqui, e segue o mesmo caminho gracioso do corte por Budget:
        // diagnóstico no stderr + "stop" no stdout (o canal lido pelo cliente IDE).
        try
        {
            var result = RunWithTimeout(action, envelope, HarnessConfig.Current.TimeoutMs);
            return (result, result == "stop" ? TraceOutcome.Stop : TraceOutcome.Instruction);
        }
        catch (HarnessTimeoutException ex)
        {
            Console.Error.WriteLine($"[harness] {ex.Message}");
            return ("stop", TraceOutcome.Timeout);
        }
    }

    // A task é um Func síncrono e OPACO — não coopera com CancellationToken. O .NET moderno
    // não aborta código síncrono travado com segurança (Thread.Abort foi removido), então o
    // único timeout preemptivo real é rodá-la noutro thread e ABANDONAR o que travar.
    // Task.Run usa o threadpool (threads background): quando o processo single-shot sai com
    // "stop", ele encerra mesmo com a task fujona ainda rodando — um new Thread foreground
    // travaria o encerramento. GetAwaiter().GetResult() (não .Result) re-lança a exceção
    // original da task sem embrulhá-la em AggregateException, preservando o comportamento atual.
    private static string RunWithTimeout(Func<Envelope?, string> action, Envelope? envelope, int timeoutMs)
    {
        if (timeoutMs <= 0)
            return action(envelope); // guarda desligada — sem overhead de thread

        var task = Task.Run(() => action(envelope));
        if (!task.Wait(timeoutMs))
            throw new HarnessTimeoutException(timeoutMs);
        return task.GetAwaiter().GetResult(); // task já concluída aqui — não bloqueia; só re-lança
    }

    private static string ErrorInstruction(string reason, IReadOnlyDictionary<string, Func<Envelope?, string>> actions)
    {
        var valid = string.Join(", ", actions.Keys);
        return $"ERRO no protocolo do harness: {reason} Comandos válidos: {valid}. "
            + "Revise o campo 'value' do seu JSON de resposta (responda apenas com o JSON, "
            + "sem cercas de código nem comentários) e reenvie o comando.";
    }
}
