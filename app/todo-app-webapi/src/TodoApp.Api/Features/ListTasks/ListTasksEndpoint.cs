using Npgsql;

namespace TodoApp.Api.Features.ListTasks;

public static class ListTasksEndpoint
{
    public static IEndpointRouteBuilder MapListTasks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/tasks", async Task<IResult> (
            string? status,
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken) =>
        {
            if (!ListTasksProjection.TryParseStatusFilter(status, out var statusFilter))
            {
                return Results.BadRequest(new { error = "Status must be pending, completed, or all." });
            }

            await using var command = CreateCommand(dataSource, statusFilter);
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

    private static NpgsqlCommand CreateCommand(NpgsqlDataSource dataSource, TaskStatusFilter statusFilter)
    {
        if (statusFilter is TaskStatusFilter.All)
        {
            return dataSource.CreateCommand("""
                SELECT id, title, is_complete
                FROM tasks
                ORDER BY id;
                """);
        }

        var command = dataSource.CreateCommand("""
            SELECT id, title, is_complete
            FROM tasks
            WHERE is_complete = @isComplete
            ORDER BY id;
            """);
        command.Parameters.AddWithValue(
            "isComplete",
            statusFilter is TaskStatusFilter.Completed);

        return command;
    }
}

public sealed record ListTaskResponse(long Id, string Status, string Title);
