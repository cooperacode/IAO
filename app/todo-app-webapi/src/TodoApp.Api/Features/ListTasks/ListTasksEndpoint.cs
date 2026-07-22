using Npgsql;

namespace TodoApp.Api.Features.ListTasks;

public static class ListTasksEndpoint
{
    public static IEndpointRouteBuilder MapListTasks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/tasks", async (
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken) =>
        {
            await using var command = dataSource.CreateCommand("""
                SELECT id, title, is_complete
                FROM tasks
                ORDER BY id;
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var tasks = new List<ListTaskResponse>();
            while (await reader.ReadAsync(cancellationToken))
            {
                tasks.Add(ListTasksProjection.ToResponse(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetBoolean(2)));
            }

            return Results.Ok(tasks);
        });

        return endpoints;
    }
}

public sealed record ListTaskResponse(long Id, string Status, string Title);
