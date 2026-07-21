var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { Name = "TodoApp API" }));

app.Run();

public partial class Program;
