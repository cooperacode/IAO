using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace TodoApp.IntegrationTests;

public sealed class PersistenceAcrossRestartTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=54329;Database=todoapp;Username=todoapp;Password=todoapp";

    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(ConnectionString);

    [Fact]
    public async Task Task_created_before_api_restart_is_still_listed_after_new_api_host_starts()
    {
        using (var firstFactory = CreateFactory())
        {
            using var firstClient = firstFactory.CreateClient();
            var createResponse = await firstClient.PostAsJsonAsync("/tasks", new AddTaskRequest("Persistent task"));

            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        }

        using (var secondFactory = CreateFactory())
        {
            using var secondClient = secondFactory.CreateClient();
            var listResponse = await secondClient.GetAsync("/tasks");

            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            var tasks = await listResponse.Content.ReadFromJsonAsync<List<ListTaskResponse>>();
            Assert.NotNull(tasks);
            var task = Assert.Single(tasks);
            Assert.Equal(1, task.Id);
            Assert.Equal("pending", task.Status);
            Assert.Equal("Persistent task", task.Title);
        }
    }

    public async Task InitializeAsync()
    {
        await using var command = _dataSource.CreateCommand("TRUNCATE TABLE tasks RESTART IDENTITY;");
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
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
    }

    private sealed record AddTaskRequest(string? Title);

    private sealed record ListTaskResponse(long Id, string Status, string Title);
}
