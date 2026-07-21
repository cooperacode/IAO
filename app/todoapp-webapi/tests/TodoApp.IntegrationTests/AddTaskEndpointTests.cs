using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace TodoApp.IntegrationTests;

public sealed class AddTaskEndpointTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=54329;Database=todoapp;Username=todoapp;Password=todoapp";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly NpgsqlDataSource _dataSource;

    public AddTaskEndpointTests()
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
    public async Task PostTasks_persists_pending_task_in_postgres()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/tasks", new AddTaskRequest("  Buy milk  "));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/tasks/1", response.Headers.Location?.ToString());

        var created = await response.Content.ReadFromJsonAsync<CreatedTaskResponse>();
        Assert.NotNull(created);
        Assert.Equal(1, created.Id);
        Assert.Equal("pending", created.Status);
        Assert.Equal("Buy milk", created.Title);

        await using var command = _dataSource.CreateCommand("""
            SELECT id, title, is_completed
            FROM tasks
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("id", created.Id);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(created.Id, reader.GetInt64(0));
        Assert.Equal("Buy milk", reader.GetString(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(await reader.ReadAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PostTasks_rejects_empty_title(string title)
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/tasks", new AddTaskRequest(title));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("Title is required.", error?.Error);
        Assert.Equal(0, await CountTasksAsync());
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

    private async Task<long> CountTasksAsync()
    {
        await using var command = _dataSource.CreateCommand("SELECT COUNT(*) FROM tasks;");
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private sealed record AddTaskRequest(string? Title);

    private sealed record CreatedTaskResponse(long Id, string Status, string Title);

    private sealed record ErrorResponse(string Error);
}
