var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/operacoes/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();

// Exposto para WebApplicationFactory nos testes de integração.
public partial class Program { }
