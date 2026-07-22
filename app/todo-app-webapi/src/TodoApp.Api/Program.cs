using Npgsql;
using TodoApp.Api.Features.AddTask;
using TodoApp.Api.Features.CompleteTask;
using TodoApp.Api.Features.ListTasks;
using TodoApp.Api.Shared;

var builder = WebApplication.CreateBuilder(args);

var connectionString = Database.ResolveConnectionString(
    builder.Configuration[Database.ConnectionStringEnvironmentVariable]);
var dataSource = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(dataSource);

var app = builder.Build();

await Database.EnsureCreatedAsync(dataSource);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAddTask();
app.MapCompleteTask();
app.MapListTasks();

app.Run();

public partial class Program;
