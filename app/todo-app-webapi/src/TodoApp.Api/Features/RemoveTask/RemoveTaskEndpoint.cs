using Npgsql;

namespace TodoApp.Api.Features.RemoveTask;

public static class RemoveTaskEndpoint
{
    public static IEndpointRouteBuilder MapRemoveTask(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete("/tasks/{id:long}", async Task<IResult> (
            long id,
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken) =>
        {
            await using var command = dataSource.CreateCommand("""
                DELETE FROM tasks
                WHERE id = @id;
                """);
            command.Parameters.AddWithValue("id", id);

            var rowsDeleted = await command.ExecuteNonQueryAsync(cancellationToken);
            if (!RemoveTaskRules.WasRemoved(rowsDeleted))
            {
                return Results.NotFound(new { error = "Task not found." });
            }

            return Results.NoContent();
        });

        return endpoints;
    }
}
