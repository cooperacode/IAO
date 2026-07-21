using Npgsql;

namespace TodoApp.Api.Tasks.AddTask;

public static class AddTaskEndpoint
{
    public static void MapAddTask(this IEndpointRouteBuilder app)
    {
        app.MapPost("/tasks", AddTaskAsync);
    }

    private static async Task<IResult> AddTaskAsync(
        AddTaskRequest request,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var validation = AddTaskRules.ValidateTitle(request.Title);
        if (!validation.IsValid)
        {
            return Results.BadRequest(new ErrorResponse(validation.Error!));
        }

        await using var command = dataSource.CreateCommand("""
            INSERT INTO tasks (title, is_completed)
            VALUES (@title, FALSE)
            RETURNING id, title, is_completed;
            """);
        command.Parameters.AddWithValue("title", validation.Title!);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Postgres did not return the inserted task.");
        }

        var response = new AddTaskResponse(
            reader.GetInt64(0),
            reader.GetBoolean(2) ? "completed" : "pending",
            reader.GetString(1));

        return Results.Created($"/tasks/{response.Id}", response);
    }
}

public static class AddTaskRules
{
    public static AddTaskValidation ValidateTitle(string? title)
    {
        var normalizedTitle = title?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return new AddTaskValidation(false, null, "Title is required.");
        }

        return new AddTaskValidation(true, normalizedTitle, null);
    }
}

public sealed record AddTaskRequest(string? Title);

public sealed record AddTaskResponse(long Id, string Status, string Title);

public sealed record AddTaskValidation(bool IsValid, string? Title, string? Error);

public sealed record ErrorResponse(string Error);
