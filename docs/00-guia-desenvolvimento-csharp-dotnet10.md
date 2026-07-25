# Guia de desenvolvimento — C# 14 e .NET 10

> Contexto normativo para o LLM que implementará e evoluirá o TodoApp WebAPI.
> Atualizado em 2026-07-25.

## 1. Como usar este documento

Leia este guia antes de escrever código. As palavras **DEVE**, **NÃO DEVE**,
**PREFIRA** e **EVITE** indicam o peso da orientação.

Ordem de precedência:

1. o brief ativo define comportamento e escopo;
2. ADRs aceitos definem decisões arquiteturais;
3. este guia define práticas de implementação;
4. diagramas C4 ajudam a localizar responsabilidades;
5. exemplos deste documento ilustram o padrão, mas não criam requisito de produto.

Se duas fontes realmente conflitarem, não escolha silenciosamente: preserve o
comportamento já coberto por testes, registre a divergência e proponha um ADR ou uma
correção do brief. Não amplie o escopo para “melhorar” algo que a feature não pede.

Documentos que formam o contexto do App:

- [brief base](202607211323-todo-app-brief.md);
- [ADR de Vertical Slice](adr-0001-vertical-slice.md);
- [diagrama de componentes](c4-diagrama-componentes.md);
- [brief de notificação por e-mail](202607220120-todo-app-notificacao-email-status-brief.md);
- [ADR de notificação por e-mail](adr-0002-notificacao-email.md).

## 2. Baseline técnico obrigatório

- Runtime e TFM: **.NET 10 LTS**, com `<TargetFramework>net10.0</TargetFramework>`.
- Linguagem: **C# 14 estável**, selecionada por padrão pelo TFM `net10.0`.
- API: ASP.NET Core 10 com Minimal APIs.
- Banco: PostgreSQL real, iniciado por Docker Compose.
- Acesso a dados: Npgsql e SQL explícito por slice.
- Testes: xUnit, em projetos separados de unidade e integração.
- Testes HTTP: `WebApplicationFactory<Program>` contra o PostgreSQL real.
- Serialização: `System.Text.Json`, padrão web `camelCase`.
- Documentação HTTP: OpenAPI oficial do ASP.NET Core.

Não use versões `preview`. Não configure `<LangVersion>latest</LangVersion>`:
o resultado depende do SDK instalado e torna o build não reprodutível. O TFM
`net10.0` já seleciona C# 14. Se for indispensável fixar a linguagem, use `14.0`.

Fixe em `global.json` uma versão do SDK 10.x que tenha sido validada no projeto e
permita somente o roll-forward acordado pela equipe. Atualizar SDK ou pacote é uma
mudança deliberada: restaure, compile e rode toda a suíte antes de versioná-la.
Nunca invente uma versão de pacote. Use uma versão estável compatível com .NET 10,
resolvida pelo NuGet, e versione o número exato restaurado.

Configuração mínima comum:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
  </PropertyGroup>
</Project>
```

Centralize as propriedades comuns em `Directory.Build.props` quando houver mais de
um projeto. Centralize versões em `Directory.Packages.props`; não duplique versões
entre `.csproj`. Mantenha um `.editorconfig` versionado e faça o formatador e os
analisadores participarem do comando de verificação.

## 3. Estrutura esperada

```text
app/todoapp-webapi/
├── TodoApp.sln
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── docker-compose.yml
├── init.sh
├── verify-feature.sh
├── src/
│   └── TodoApp.Api/
│       ├── Features/
│       │   ├── CreateTask/
│       │   ├── ListTasks/
│       │   ├── CompleteTask/
│       │   ├── EditTask/
│       │   └── RemoveTask/
│       ├── Infrastructure/
│       ├── Notifications/
│       └── Program.cs
└── tests/
    ├── TodoApp.UnitTests/
    └── TodoApp.IntegrationTests/
```

Cada pasta em `Features` é uma fatia vertical e contém somente o necessário para
seu endpoint: contrato HTTP, validação/regra pura, handler e SQL. Um arquivo por
conceito é aceitável quando o slice crescer; não fragmente um slice pequeno apenas
para simular camadas.

`Infrastructure` contém mecanismos genuinamente compartilhados, como criação do
`NpgsqlDataSource` e inicialização do schema. `Notifications` é uma fronteira externa
pequena exigida pelo ADR-0002. Nenhuma dessas pastas deve virar uma camada genérica
de negócio.

## 4. Regras de arquitetura

### Vertical slices

Uma feature nova DEVE:

- mapear um endpoint e seus contratos dentro do próprio slice;
- manter a regra de negócio pura separável de HTTP e Postgres;
- acessar o banco diretamente pelo `NpgsqlDataSource`;
- ter testes de unidade para a regra pura quando houver decisão de negócio;
- ter teste de integração para o comportamento HTTP e a persistência real;
- alterar o shared kernel somente se pelo menos dois slices realmente precisarem
  do mesmo conceito estável.

Não crie `TaskService`, `GenericRepository<T>`, `BaseController`, MediatR, CQRS
cerimonial ou uma interface para cada classe. Uma abstração só é justificada quando
isola um efeito externo, permite uma substituição real ou representa um conceito de
domínio compartilhado. Duplicação pequena entre slices é preferível a acoplamento
prematuro.

### Dependências

As dependências apontam do endpoint para mecanismos pequenos e explícitos:

```text
requisição HTTP
    → endpoint do slice
        → regra pura
        → NpgsqlDataSource
        → efeito externo específico, quando requerido
```

`Program.cs` deve ser uma composition root curta: configura serviços, middleware e
mapeia os slices. Regra de negócio, SQL e montagem de e-mail não pertencem ali.

## 5. Práticas de C# 14

### Convenções obrigatórias

- Use nullable reference types e trate warnings de nulabilidade como defeitos.
- Use nomes em inglês no código (`CreateTask`, `Title`, `Pending`) e mantenha
  textos de usuário e documentação em português quando apropriado.
- Use namespaces file-scoped, `sealed` em classes não projetadas para herança e
  `record` para contratos imutáveis por valor.
- Prefira imutabilidade, funções pequenas e retornos antecipados.
- Passe `CancellationToken` até toda operação assíncrona de I/O.
- Use o sufixo `Async` em métodos assíncronos, exceto handlers cujo nome já seja
  estabelecido pelo framework.
- Nunca bloqueie async com `.Result`, `.Wait()` ou `.GetAwaiter().GetResult()`.
- Não use `async void`, exceto em event handlers exigidos por framework.
- Não capture `DateTime.Now`; quando tempo entrar no domínio, injete `TimeProvider`
  e persista UTC.
- Não use `dynamic`, reflexão ou `null!` para contornar desenho de tipos.
- Comentários explicam o porquê e trade-offs; o código deve explicar o quê.

Use recursos recentes quando reduzirem estado ou deixarem a intenção mais clara:

```csharp
// Record imutável para um contrato.
public sealed record TaskResponse(int Id, string Title, string Status);

// Collection expression (C# 12+, disponível em C# 14).
string[] allowedStatuses = ["pending", "completed"];

// Pattern matching em regra pura.
public static StatusFilter ParseStatus(string? value) =>
    value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => new(true, null),
        "pending" => new(true, "pending"),
        "completed" => new(true, "completed"),
        _ => new(false, null)
    };

// Raw string literal para SQL legível e sem concatenação.
const string sql = """
    SELECT id, title, status
    FROM tasks
    WHERE status = $1
    ORDER BY id;
    """;
```

Recursos específicos do C# 14 são permitidos, mas não são meta de cobertura:

```csharp
public sealed class SmtpOptions
{
    public string? Sender
    {
        get;
        init => field = value?.Trim(); // backing field gerado pelo compilador
    }
}

// A expressão da direita só é avaliada quando notification não é null.
notification?.SentAt = timeProvider.GetUtcNow();

var openGenericName = nameof(Dictionary<,>);
```

Use blocos `extension`, conversões de `Span<T>` e membros parciais somente quando o
caso concreto justificar. Não reescreva código simples apenas para exibir sintaxe
nova.

## 6. Contratos HTTP

Use um grupo `/tasks`, nomes estáveis de endpoint e resultados tipados. DTO HTTP,
modelo de persistência e mensagem de notificação são conceitos diferentes mesmo
quando possuem campos iguais.

Compatibilidade atual:

- JSON usa `camelCase`: `id`, `title`, `status`;
- status de transporte/persistência: `pending` e `completed`;
- filtro aceita `pending`, `completed` e `all`;
- alteração desses valores exige mudança explícita de contrato e atualização de
  todos os testes e documentos afetados.

Nesse baseline, “pendente” e “concluída” nos cenários escritos em português
descrevem o domínio; os valores literais do contrato são `pending` e `completed`.

Mapeamento recomendado:

```csharp
var tasks = app.MapGroup("/tasks")
    .WithTags("Tasks");

tasks.MapCreateTaskEndpoint();
tasks.MapListTasksEndpoint();
tasks.MapCompleteTaskEndpoint();
tasks.MapEditTaskEndpoint();
tasks.MapRemoveTaskEndpoint();
```

Cada handler DEVE:

- tipar os resultados esperados com `TypedResults` e `Results<T1, ...>`;
- devolver `201 Created` e `Location` ao criar;
- devolver `200 OK` para leitura/alteração bem-sucedida;
- devolver `204 No Content` ao remover;
- devolver `400 Bad Request` para entrada inválida;
- devolver `404 Not Found` quando o id não existir;
- propagar cancelamento;
- nunca expor exception, SQL, host, usuário ou senha do banco.

Configure respostas de erro consistentes:

```csharp
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] =
            context.HttpContext.TraceIdentifier;
    };
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
```

Erros esperados de validação e “não encontrado” são retornos normais do endpoint.
Falhas inesperadas passam pelo handler global e são registradas internamente.
OpenAPI deve ser gerado com `AddOpenApi` e, por padrão, exposto por `MapOpenApi`
somente em `Development`.

### Exemplo de slice

O exemplo mostra a forma; adapte nomes e respostas ao brief ativo:

```csharp
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace TodoApp.Api.Features.CreateTask;

public static class CreateTaskEndpoint
{
    public static RouteGroupBuilder MapCreateTaskEndpoint(
        this RouteGroupBuilder group)
    {
        group.MapPost("/", HandleAsync)
            .WithName("CreateTask")
            .WithSummary("Creates a pending task.");

        return group;
    }

    internal static async Task<Results<Created<TaskResponse>, ValidationProblem>>
        HandleAsync(
            CreateTaskRequest request,
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken)
    {
        var title = CreateTaskRules.NormalizeTitle(request.Title);
        if (title is null)
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["title"] = ["Title is required."]
                });
        }

        await using var command = dataSource.CreateCommand("""
            INSERT INTO tasks (title, status)
            VALUES ($1, 'pending')
            RETURNING id;
            """);
        command.Parameters.AddWithValue(title);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        var id = result is int value
            ? value
            : throw new InvalidOperationException("The database did not return an id.");

        var response = new TaskResponse(id, title, TaskStatuses.Pending);
        return TypedResults.Created($"/tasks/{id}", response);
    }
}

public sealed record CreateTaskRequest(string? Title);
public sealed record TaskResponse(int Id, string Title, string Status);

public static class TaskStatuses
{
    public const string Pending = "pending";
    public const string Completed = "completed";
}

public static class CreateTaskRules
{
    public static string? NormalizeTitle(string? title)
    {
        var normalized = title?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
```

Não compartilhe `TaskResponse` automaticamente entre todos os slices. Contratos
iguais hoje podem evoluir por motivos diferentes. Extraia somente quando a
identidade compartilhada for uma decisão consciente.

## 7. PostgreSQL e Npgsql

Registre um único `NpgsqlDataSource` no DI. Ele encapsula configuração e pooling;
abra comandos/conexões por operação e descarte-os com `await using`.

```csharp
var connectionString =
    builder.Configuration["TODO_DB_CONNECTION"]
    ?? (builder.Environment.IsDevelopment()
        ? "Host=localhost;Port=55433;Database=todoapp;Username=todoapp;Password=todoapp"
        : throw new InvalidOperationException(
            "TODO_DB_CONNECTION must be configured."));

builder.Services.AddSingleton(
    _ => NpgsqlDataSource.Create(connectionString));
```

Regras:

- sempre parametrizar valores; nunca concatenar entrada em SQL;
- preferir placeholders posicionais (`$1`, `$2`) em código novo;
- listar colunas explicitamente, sem `SELECT *`;
- usar `RETURNING` para evitar round-trip depois de `INSERT`, `UPDATE` ou `DELETE`;
- passar `CancellationToken` a `Execute*Async`, `ReadAsync` e efeitos externos;
- usar transação explícita quando uma regra exigir mais de uma escrita atômica;
- manter o SQL do caso de uso dentro do slice;
- não criar um novo `NpgsqlDataSource` por requisição;
- não registrar nem devolver connection strings;
- aplicar limite e timeout somente com valor justificado e configurável.

O schema deve ser idempotente e ter restrições que defendam invariantes essenciais:

```sql
CREATE TABLE IF NOT EXISTS tasks (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title text NOT NULL CHECK (length(btrim(title)) > 0),
    status text NOT NULL DEFAULT 'pending',
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    updated_at timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT tasks_status_check
        CHECK (status IN ('pending', 'completed'))
);
```

A aplicação da primeira versão pode executar esse schema na inicialização, como
permitido pelo brief. Se houver mais de uma revisão de schema, introduza migrações
versionadas antes que uma sequência de `ALTER TABLE IF ...` fique implícita no
startup. Falha ao preparar o banco deve impedir a API de anunciar que está pronta.

O container do Compose é infraestrutura local. Use imagem com versão fixada, volume
nomeado e `healthcheck`; não use a tag `latest`. `init.sh` deve executar
`docker compose up -d --wait` antes de restore/build/test.

## 8. Configuração, segredos e notificação

Variáveis previstas:

| Variável | Uso |
|---|---|
| `TODO_DB_CONNECTION` | conexão PostgreSQL |
| `TODO_EMAIL_FROM` | remetente |
| `TODO_EMAIL_TO` | destinatário |

Valores locais não sensíveis podem ter default de desenvolvimento. Produção deve
falhar cedo quando uma configuração obrigatória faltar. Para conjuntos maiores,
use options tipadas, validação e `ValidateOnStart()`.

Não versione senha real, token, connection string de produção ou arquivo `.env`
com segredo. Não registre título da tarefa ou corpo de e-mail sem necessidade:
podem conter dados pessoais. Prefira logs estruturados com ids e estado:

```csharp
logger.LogInformation(
    "Task {TaskId} changed from {PreviousStatus} to {NewStatus}",
    task.Id,
    previousStatus,
    task.Status);
```

O envio de e-mail pertence ao fluxo `CompleteTask`. Use interfaces estreitas
(`IEmailNotifier` e, se necessário, `IEmailClient`) porque SMTP é um efeito externo
realmente substituível em teste. A implementação inicial é síncrona e best-effort:
persista a mudança antes de enviar; falha de envio não desfaz a atualização já
confirmada, deve ser registrada sem expor conteúdo sensível. Não adicione fila,
retry distribuído ou provedor de produção sem novo requisito/ADR.

Idempotência importa: completar uma tarefa já concluída devolve sucesso, mas não
deve enviar nova notificação porque não houve transição de status.

## 9. Testes

### Unidade

Teste regras puras sem HTTP, DI ou Postgres. Um teste deve ser rápido, determinístico
e legível no padrão Arrange–Act–Assert. Nomeie pelo comportamento:

```csharp
public sealed class CreateTaskRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeTitleReturnsNullWhenTitleHasNoContent(string? title)
    {
        var result = CreateTaskRules.NormalizeTitle(title);

        Assert.Null(result);
    }

    [Fact]
    public void NormalizeTitleTrimsOuterWhitespace()
    {
        var result = CreateTaskRules.NormalizeTitle("  Buy milk  ");

        Assert.Equal("Buy milk", result);
    }
}
```

Não teste detalhe privado, chamada de método ou porcentagem de cobertura por si.
Teste entrada, saída, invariantes e casos de borda. Um mock não deve reproduzir a
lógica que deveria estar sob teste.

### Integração

O teste de integração chama a API por `HttpClient`, hospedada por
`WebApplicationFactory<Program>`, e verifica o mesmo PostgreSQL iniciado pelo
Compose. Não substitua o banco por provider em memória, SQLite ou mock.

Para cada endpoint, cubra ao menos:

- caminho feliz e status HTTP;
- corpo e headers relevantes;
- estado realmente persistido;
- entrada inválida;
- id inexistente, quando aplicável;
- invariantes e idempotência descritas no brief.

Isole dados com `TRUNCATE tasks RESTART IDENTITY` antes/depois do caso ou use outro
mecanismo equivalente. Enquanto a suíte compartilhar uma tabela, desabilite
paralelismo entre testes de integração. Não dependa da ordem dos testes.

Em .NET 10, o suporte de teste para top-level statements gera o `Program` público
necessário ao `WebApplicationFactory`; não adicione `public partial class Program`
sem o compilador/analisador exigir.

Doubles são proibidos para o PostgreSQL, mas permitidos na fronteira de e-mail.
Substitua somente `IEmailClient` na factory e verifique a mensagem capturada; ainda
exercite endpoint, regra e banco reais.

## 10. Observabilidade, segurança e desempenho

- Use logging estruturado e níveis coerentes; não use interpolação em templates.
- Inclua `traceId` em `ProblemDetails`, sem stack trace na resposta.
- Não capture `Exception` para ignorá-la. Trate apenas quando puder traduzir,
  compensar ou acrescentar contexto útil.
- Valide no limite HTTP e reforce invariantes importantes no banco.
- Não habilite CORS, autenticação, rate limiting ou cache sem requisito. A ausência
  de autenticação é escopo deliberado do App atual, não um convite para uma solução
  parcial.
- Prefira uma operação SQL clara a carregar dados para filtrá-los em memória.
- Meça antes de otimizar. Não introduza cache, pooling próprio ou `Span<T>` sem
  gargalo demonstrado.
- Preserve a semântica HTTP e a legibilidade antes de micro-otimizações.

## 11. Fluxo de implementação para o LLM

Para cada feature:

1. leia o brief ativo, ADRs e os testes existentes;
2. confirme o baseline com `./init.sh`;
3. implemente somente uma fatia vertical;
4. escreva/ajuste primeiro os testes que provam a mudança;
5. compile cedo e trate todos os warnings;
6. rode o teste focado durante a iteração;
7. rode `./verify-feature.sh <id>` antes de concluir;
8. revise o diff para remover código fora do escopo, segredo, artefato `bin/obj`
   e duplicação acidental;
9. atualize o registro de progresso conforme o harness.

Não declare sucesso com base apenas em leitura do código. A evidência mínima é build
verde, testes de unidade verdes e teste HTTP real verde contra Postgres.

## 12. Definition of Done

- [ ] comportamento e status HTTP correspondem ao brief;
- [ ] arquitetura respeita o ADR-0001 e, quando aplicável, o ADR-0002;
- [ ] `net10.0`, C# 14 estável, nullable e analisadores permanecem ativos;
- [ ] regra pura possui teste de unidade;
- [ ] endpoint possui teste de integração HTTP + Postgres real;
- [ ] SQL está parametrizado e recebe `CancellationToken`;
- [ ] respostas de erro são consistentes e não vazam detalhes;
- [ ] configuração externa não contém segredo versionado;
- [ ] OpenAPI reflete os contratos tipados;
- [ ] `./verify-feature.sh <id>` passa;
- [ ] diff não contém mudança oportunista nem `bin/`, `obj/` ou logs.

## Referências oficiais

- [.NET 10 — visão geral](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/overview)
- [Novidades do C# 14](https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-14)
- [Configuração da versão da linguagem C#](https://learn.microsoft.com/dotnet/csharp/language-reference/configure-language-version)
- [Novidades do ASP.NET Core 10](https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-10.0)
- [Respostas em Minimal APIs](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/responses)
- [Tratamento de erros em APIs ASP.NET Core](https://learn.microsoft.com/aspnet/core/fundamentals/error-handling-api)
- [OpenAPI no ASP.NET Core](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/overview)
- [Testes de integração no ASP.NET Core](https://learn.microsoft.com/aspnet/core/test/integration-tests)
- [Boas práticas de testes de unidade .NET](https://learn.microsoft.com/dotnet/core/testing/unit-testing-best-practices)
- [Options pattern e validação](https://learn.microsoft.com/dotnet/core/extensions/options)
- [Uso básico do NpgsqlDataSource](https://www.npgsql.org/doc/basic-usage.html)
