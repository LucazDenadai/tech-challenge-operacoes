using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OficinaMecanica.Operacoes.API.Extensions;
using OficinaMecanica.Operacoes.API.Filters;
using OficinaMecanica.Operacoes.API.Middleware;
using OficinaMecanica.Operacoes.Infrastructure;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Seed;

var builder = WebApplication.CreateBuilder(args);

// ── Logging estruturado em JSON (escopos carregam o CorrelationId) ─────────────
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.TimestampFormat = "O";
    o.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
});

// ── OpenTelemetry (traces OTLP, métricas Prometheus) ───────────────────────────
builder.AddOpenTelemetry("OficinaMecanica.Operacoes");

// ── Infrastructure (banco de Operações, casos de uso) e mensageria da Saga ─────
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddMensageria(builder.Configuration);

// ── Autenticação: valida o JWT emitido pelo OS (mesmo issuer e chave, ADR-015) ──
var jwtKey = (builder.Configuration["Jwt:Key"] is { Length: > 0 } k ? k : null)
    ?? Environment.GetEnvironmentVariable("JWT_KEY")
    ?? throw new InvalidOperationException("JWT_KEY não configurado.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? Environment.GetEnvironmentVariable("JWT_ISSUER"),
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? Environment.GetEnvironmentVariable("JWT_AUDIENCE"),
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();

// ── CORS ───────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>();
        if (origins is { Length: > 0 })
            policy.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader();
        else if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Test"))
            policy.WithOrigins("http://localhost", "https://localhost").AllowAnyMethod().AllowAnyHeader();
        else
            throw new InvalidOperationException("Cors:Origins não configurado. Defina ao menos uma origem permitida.");
    });
});

// ── Controllers + Filters ──────────────────────────────────────────────────────
builder.Services.AddControllers(options =>
{
    options.Filters.Add<GlobalExceptionFilter>();
})
.AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var erros = context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value!.Errors.Select(e => e.ErrorMessage).ToArray());

        var resultado = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Dados inválidos",
            Detail = "Um ou mais campos não passaram na validação.",
        };
        resultado.Extensions["erros"] = erros;

        return new BadRequestObjectResult(resultado);
    };
});

// ── Health Checks: liveness sem dependências; readiness com os dois stores e o broker ─
var healthChecks = builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("postgres", tags: ["ready"])
    .AddCheck<DynamoDbHealthCheck>("dynamodb", tags: ["ready"]);
if (builder.Configuration.GetValue<bool>("RabbitMq:Enabled"))
    healthChecks.AddCheck<ConsumidorSagaHealthCheck>("rabbitmq", tags: ["ready"]);

// ── Swagger ────────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Oficina Mecânica — Operações API",
        Version = "v1",
        Description = "Serviço Operações da Fase 4: catálogo de peças e serviços, saldos por filial, disponibilidade, movimentações "
                      + "e execução da OS (diagnóstico, fila, reparo, conclusão e falha). "
                      + "Reserva, consumo, liberação e início da execução vêm só pelos comandos da Saga (AsyncAPI)."
    });

    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml"));

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT de funcionário emitido pelo serviço OS (POST /os/auth/login)."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

var app = builder.Build();

// ── Migrations + seed de demonstração (ADR-015: banco vazio, sem migração de dados) ─
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await SeedDemonstracao.ExecutarAsync(db);
}

// ── Middleware pipeline ────────────────────────────────────────────────────────
app.UseMiddleware<CorrelationIdMiddleware>();

app.UseSwagger(c => c.RouteTemplate = "operacoes/swagger/{documentName}/swagger.json");
app.UseSwaggerUI(c =>
{
    c.RoutePrefix = "operacoes/swagger";
    c.SwaggerEndpoint("/operacoes/swagger/v1/swagger.json", "Operações API v1");
});

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/operacoes/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/operacoes/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapPrometheusScrapingEndpoint();
app.MapControllers();

await app.RunAsync();

public partial class Program { protected Program() { } }
