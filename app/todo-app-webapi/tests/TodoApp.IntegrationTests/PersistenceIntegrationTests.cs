using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using TodoApp.Api.Features.AddTask;
using TodoApp.Api.Features.ListTasks;
using TodoApp.Api.Shared;

namespace TodoApp.IntegrationTests;

public class PersistenceIntegrationTests
{
    [Fact]
    public async Task Task_created_before_api_restart_is_listed_after_new_api_instance()
    {
        long createdId;

        await using (var firstFactory = new WebApplicationFactory<Program>())
        {
            using var firstClient = firstFactory.CreateClient();
            await ClearTasksAsync();

            var createResponse = await firstClient.PostAsJsonAsync(
                "/tasks",
                new AddTaskRequest("Persist across restart"));

            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            var created = await createResponse.Content.ReadFromJsonAsync<AddTaskResponse>();
            Assert.NotNull(created);
            createdId = created.Id;
        }

        await using var secondFactory = new WebApplicationFactory<Program>();
        using var secondClient = secondFactory.CreateClient();

        var listResponse = await secondClient.GetAsync("/tasks");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var tasks = await listResponse.Content.ReadFromJsonAsync<ListTaskResponse[]>();
        Assert.NotNull(tasks);
        var persisted = Assert.Single(tasks);
        Assert.Equal(createdId, persisted.Id);
        Assert.Equal("pending", persisted.Status);
        Assert.Equal("Persist across restart", persisted.Title);
    }

    private static async Task ClearTasksAsync()
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("TRUNCATE TABLE tasks RESTART IDENTITY;");
        await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var connectionString = Database.ResolveConnectionString(
            Environment.GetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable));
        return NpgsqlDataSource.Create(connectionString);
    }
}
