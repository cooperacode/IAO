using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using TodoApp.Api.Shared;

namespace TodoApp.IntegrationTests;

public class RemoveTaskIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task DeleteTask_removes_existing_task_from_postgres()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();
        var id = await InsertTaskAsync("Remove me");

        var response = await client.DeleteAsync($"/tasks/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await TaskExistsAsync(id));
    }

    [Fact]
    public async Task DeleteTask_returns_not_found_for_missing_task()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();

        var response = await client.DeleteAsync("/tasks/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task ClearTasksAsync()
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("TRUNCATE TABLE tasks RESTART IDENTITY;");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertTaskAsync(string title)
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("""
            INSERT INTO tasks (title)
            VALUES (@title)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("title", title);

        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Postgres did not return inserted task id."));
    }

    private static async Task<bool> TaskExistsAsync(long id)
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM tasks
                WHERE id = @id
            );
            """);
        command.Parameters.AddWithValue("id", id);

        return (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Postgres did not return existence check."));
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var connectionString = Database.ResolveConnectionString(
            Environment.GetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable));
        return NpgsqlDataSource.Create(connectionString);
    }
}
