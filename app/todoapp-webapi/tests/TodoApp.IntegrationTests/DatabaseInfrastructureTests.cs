using Npgsql;

namespace TodoApp.IntegrationTests;

public class DatabaseInfrastructureTests
{
    [Fact]
    public async Task Postgres_schema_contains_tasks_table()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'tasks'
            ORDER BY ordinal_position;
            """;

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        foreach (var requiredColumn in new[] { "id", "title", "is_completed", "created_at", "completed_at" })
        {
            Assert.Contains(requiredColumn, columns);
        }
    }

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("TODO_DB_CONNECTION")
        ?? "Host=localhost;Port=54329;Database=todoapp;Username=todoapp;Password=todoapp";
}
