using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

namespace OficinaMecanica.Operacoes.IntegrationTests.Api;

// Sobe a API real (Program.cs, migrations e seed) contra um banco PostgreSQL vazio do Testcontainers.
public class OperacoesApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    private const string JwtKey = "chave-jwt-de-teste-com-mais-de-32-caracteres";
    private const string JwtIssuer = "oficina-atendimento";
    private const string JwtAudience = "oficina-atendimento-api";

    public string ConnectionString => connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("Jwt:Issuer", JwtIssuer);
        builder.UseSetting("Jwt:Audience", JwtAudience);
        builder.UseSetting("RabbitMq:Enabled", "false");
        builder.UseSetting("Jaeger:Endpoint", "http://localhost:4318");
    }

    // Token no formato emitido pelo OS para funcionários (sub, email, role), mesma chave e issuer (ADR-015).
    public HttpClient Funcionario(string perfil) => ComToken(
    [
        new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
        new Claim(JwtRegisteredClaimNames.Email, $"{perfil.ToLowerInvariant()}@oficina.example"),
        new Claim("role", perfil)
    ]);

    // Token no formato da Lambda de CPF: role Cliente. Não deve acessar Operações.
    public HttpClient ClienteFinal() => ComToken([new Claim("cliente_id", Guid.NewGuid().ToString()), new Claim("role", "Cliente")]);

    private HttpClient ComToken(Claim[] claims)
    {
        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256));

        var http = CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return http;
    }
}
