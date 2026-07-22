using Npgsql;

namespace TodoApp.Api.Features.CompleteTask;

public static class CompleteTaskEndpoint
{
    public static IEndpointRouteBuilder MapCompleteTask(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPatch("/tasks/{id:long}/complete", async Task<IResult> (
            long id,
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken) =>
        {
            await using var command = dataSource.CreateCommand("""
                UPDATE tasks
                SET is_complete = @isComplete
                WHERE id = @id
                RETURNING id, title, is_complete;
                """);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("isComplete", CompleteTaskRules.ApplyTransition(currentlyComplete: false));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return Results.NotFound(new { error = "Task not found." });
            }

            var response = new CompleteTaskResponse(
                reader.GetInt64(0),
                CompleteTaskRules.ToStatus(reader.GetBoolean(2)),
                reader.GetString(1));

            return Results.Ok(response);
        });

        return endpoints;
    }
}

public sealed record CompleteTaskResponse(long Id, string Status, string Title);
