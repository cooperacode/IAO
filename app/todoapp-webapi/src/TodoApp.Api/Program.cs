using Npgsql;
using TodoApp.Api.Shared;
using TodoApp.Api.Tasks.AddTask;
using TodoApp.Api.Tasks.CompleteTask;
using TodoApp.Api.Tasks.ListTasks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(_ =>
    NpgsqlDataSource.Create(TodoDb.GetConnectionString(builder.Configuration)));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { Name = "TodoApp API" }));
app.MapAddTask();
app.MapCompleteTask();
app.MapListTasks();

app.Run();

public partial class Program;
