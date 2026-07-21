using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace TodoApp.IntegrationTests;

public sealed class RemoveTaskEndpointTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=54329;Database=todoapp;Username=todoapp;Password=todoapp";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly NpgsqlDataSource _dataSource;

    public RemoveTaskEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TODO_DB_CONNECTION"] = ConnectionString
                    });
                });
            });

        _dataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    [Fact]
    public async Task DeleteTask_removes_existing_task_from_postgres()
    {
        var firstId = await SeedTaskAsync("Remove me");
        var secondId = await SeedTaskAsync("Keep me");
        using var client = _factory.CreateClient();

        var response = await client.DeleteAsync($"/tasks/{firstId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var remaining = await ReadTasksAsync();
        var task = Assert.Single(remaining);
        Assert.Equal(secondId, task.Id);
        Assert.Equal("Keep me", task.Title);
    }

    [Fact]
    public async Task DeleteTask_returns_not_found_for_missing_task()
    {
        using var client = _factory.CreateClient();

        var response = await client.DeleteAsync("/tasks/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<RemoveTaskErrorResponse>();
        Assert.Equal("Task not found.", error?.Error);
    }

    public async Task InitializeAsync()
    {
        await using var command = _dataSource.CreateCommand("TRUNCATE TABLE tasks RESTART IDENTITY;");
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _dataSource.DisposeAsync();
    }

    private async Task<long> SeedTaskAsync(string title)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO tasks (title, is_completed)
            VALUES (@title, FALSE)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("title", title);
        return (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Task seed failed."));
    }

    private async Task<List<TaskRow>> ReadTasksAsync()
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT id, title
            FROM tasks
            ORDER BY id;
            """);

        var tasks = new List<TaskRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tasks.Add(new TaskRow(reader.GetInt64(0), reader.GetString(1)));
        }

        return tasks;
    }

    private sealed record TaskRow(long Id, string Title);

    private sealed record RemoveTaskErrorResponse(string Error);
}
