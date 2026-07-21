using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace TodoApp.IntegrationTests;

public sealed class CompleteTaskEndpointTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=54329;Database=todoapp;Username=todoapp;Password=todoapp";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly NpgsqlDataSource _dataSource;

    public CompleteTaskEndpointTests()
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
    public async Task PatchComplete_marks_task_as_completed_in_postgres()
    {
        var taskId = await SeedTaskAsync("Task to complete", isCompleted: false);
        using var client = _factory.CreateClient();

        var response = await client.PatchAsync($"/tasks/{taskId}/complete", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completed = await response.Content.ReadFromJsonAsync<CompleteTaskResponse>();
        Assert.NotNull(completed);
        Assert.Equal(taskId, completed.Id);
        Assert.Equal("completed", completed.Status);
        Assert.Equal("Task to complete", completed.Title);

        await using var command = _dataSource.CreateCommand("""
            SELECT is_completed, completed_at IS NOT NULL
            FROM tasks
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("id", taskId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task PatchComplete_returns_not_found_for_missing_task()
    {
        using var client = _factory.CreateClient();

        var response = await client.PatchAsync("/tasks/999/complete", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<CompleteTaskErrorResponse>();
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

    private async Task<long> SeedTaskAsync(string title, bool isCompleted)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO tasks (title, is_completed)
            VALUES (@title, @isCompleted)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("isCompleted", isCompleted);
        return (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Task seed failed."));
    }

    private sealed record CompleteTaskResponse(long Id, string Status, string Title);

    private sealed record CompleteTaskErrorResponse(string Error);
}
