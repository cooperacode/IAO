using Npgsql;
using TodoApp.Api.Shared;
using TodoApp.Api.Tasks.AddTask;
using TodoApp.Api.Tasks.CompleteTask;
using TodoApp.Api.Tasks.EditTask;
using TodoApp.Api.Tasks.ListTasks;
using TodoApp.Api.Tasks.RemoveTask;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(_ =>
    NpgsqlDataSource.Create(TodoDb.GetConnectionString(builder.Configuration)));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { Name = "TodoApp API" }));
app.MapAddTask();
app.MapCompleteTask();
app.MapEditTask();
app.MapListTasks();
app.MapRemoveTask();

app.Run();

public partial class Program;
