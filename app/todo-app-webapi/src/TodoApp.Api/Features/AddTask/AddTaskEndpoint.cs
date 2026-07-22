using Npgsql;

namespace TodoApp.Api.Features.AddTask;

public static class AddTaskEndpoint
{
    public static IEndpointRouteBuilder MapAddTask(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/tasks", async Task<IResult> (
            AddTaskRequest request,
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken) =>
        {
            if (!AddTaskRules.TryNormalizeTitle(request.Title, out var title))
            {
                return Results.BadRequest(new { error = "Title is required." });
            }

            await using var command = dataSource.CreateCommand("""
                INSERT INTO tasks (title)
                VALUES (@title)
                RETURNING id;
                """);
            command.Parameters.AddWithValue("title", title);

            var id = (long)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Postgres did not return the inserted task id."));
            var response = new AddTaskResponse(id, "pending", title);

            return Results.Created($"/tasks/{id}", response);
        });

        return endpoints;
    }
}

public sealed record AddTaskRequest(string? Title);

public sealed record AddTaskResponse(long Id, string Status, string Title);
