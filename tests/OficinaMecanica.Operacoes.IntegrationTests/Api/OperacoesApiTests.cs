using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Seed;
using OficinaMecanica.Operacoes.IntegrationTests.Fixtures;

namespace OficinaMecanica.Operacoes.IntegrationTests.Api;

// Testes de API do CARD-38a: happy path, validação, autorização e falhas, contra a API e o banco reais.
[Collection(PostgresCollection.Nome)]
public class OperacoesApiTests : IAsyncLifetime
{
    private static readonly Guid Filial = SeedDemonstracao.FilialDemoId;

    private readonly OperacoesApiFactory _factory;

    public OperacoesApiTests(PostgresFixture fixture) => _factory = new OperacoesApiFactory(fixture.NovoBancoVazio());

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ── Autorização ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SemToken_Retorna401()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/operacoes/pecas")).StatusCode);

    [Fact]
    public async Task TokenDeCliente_Retorna403()
        => Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClienteFinal().GetAsync("/operacoes/pecas")).StatusCode);

    [Theory]
    [InlineData("Mecanico")]
    [InlineData("Atendente")]
    public async Task SoAdminAlteraCatalogoEEstoque(string perfil)
    {
        var http = _factory.Funcionario(perfil);

        var peca = await http.PostAsJsonAsync("/operacoes/pecas", new { codigo = "X-1", nome = "X", precoTabela = 10m });
        var entrada = await http.PostAsJsonAsync("/operacoes/estoque/entradas",
            new { filialId = Filial, pecaId = Guid.NewGuid(), quantidade = 1, motivo = "Compra" });

        Assert.Equal(HttpStatusCode.Forbidden, peca.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, entrada.StatusCode);
    }

    [Fact]
    public async Task MecanicoNaoVeMovimentacoes()
        => Assert.Equal(HttpStatusCode.Forbidden,
            (await _factory.Funcionario("Mecanico").GetAsync($"/operacoes/estoque/filiais/{Filial}/movimentacoes")).StatusCode);

    // ── Catálogo ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Catalogo_SeedDisponivelParaFuncionarios()
    {
        var http = _factory.Funcionario("Mecanico");

        var pecas = await http.GetFromJsonAsync<JsonElement>("/operacoes/pecas");
        var servicos = await http.GetFromJsonAsync<JsonElement>("/operacoes/servicos");

        Assert.Equal(5, pecas.GetArrayLength());
        Assert.Equal(4, servicos.GetArrayLength());
        Assert.All(pecas.EnumerateArray(), p => Assert.Equal("BRL", p.GetProperty("moeda").GetString()));
    }

    [Fact]
    public async Task Peca_CriarAtualizarEDesativar()
    {
        var http = _factory.Funcionario("Admin");

        var criada = await http.PostAsJsonAsync("/operacoes/pecas", new { codigo = "amort-d", nome = "Amortecedor dianteiro", precoTabela = 320.00m });
        Assert.Equal(HttpStatusCode.Created, criada.StatusCode);
        var id = (await criada.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var atualizada = await http.PutAsJsonAsync($"/operacoes/pecas/{id}", new { nome = "Amortecedor dianteiro (par)", precoTabela = 610.00m });
        Assert.Equal(HttpStatusCode.OK, atualizada.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/operacoes/pecas/{id}")).StatusCode);
        var ativas = await http.GetFromJsonAsync<JsonElement>("/operacoes/pecas");
        var todas = await http.GetFromJsonAsync<JsonElement>("/operacoes/pecas?incluirInativas=true");
        Assert.DoesNotContain(ativas.EnumerateArray(), p => p.GetProperty("id").GetGuid() == id);
        Assert.Contains(todas.EnumerateArray(), p => p.GetProperty("codigo").GetString() == "AMORT-D");
    }

    [Fact]
    public async Task Peca_CodigoRepetido_Retorna422()
    {
        var resposta = await _factory.Funcionario("Admin").PostAsJsonAsync("/operacoes/pecas",
            new { codigo = "flt-oleo-01", nome = "Outro filtro", precoTabela = 10m });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
    }

    [Fact]
    public async Task Peca_DadosInvalidos_Retorna400ComErros()
    {
        var resposta = await _factory.Funcionario("Admin").PostAsJsonAsync("/operacoes/pecas", new { codigo = "", nome = "", precoTabela = 0m });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(corpo.GetProperty("erros").TryGetProperty("Codigo", out _));
        Assert.True(corpo.GetProperty("erros").TryGetProperty("PrecoTabela", out _));
    }

    [Fact]
    public async Task Peca_Inexistente_Retorna404()
        => Assert.Equal(HttpStatusCode.NotFound,
            (await _factory.Funcionario("Atendente").GetAsync($"/operacoes/pecas/{Guid.NewGuid()}")).StatusCode);

    [Fact]
    public async Task Servico_CriarEAtualizar()
    {
        var http = _factory.Funcionario("Admin");

        var criado = await http.PostAsJsonAsync("/operacoes/servicos", new { nome = "Troca de amortecedores", preco = 200m, tempoConclusaoMinutos = 120 });
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var id = (await criado.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var atualizado = await http.PutAsJsonAsync($"/operacoes/servicos/{id}", new { nome = "Troca de amortecedores", preco = 220m, tempoConclusaoMinutos = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, atualizado.StatusCode);
    }

    // ── Estoque ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Saldos_DaFilialDemo()
    {
        var http = _factory.Funcionario("Atendente");

        var filiais = await http.GetFromJsonAsync<JsonElement>("/operacoes/estoque/filiais");
        var saldos = await http.GetFromJsonAsync<JsonElement>($"/operacoes/estoque/filiais/{Filial}/saldos");

        Assert.Equal(Filial, filiais.EnumerateArray().Single().GetProperty("id").GetGuid());
        var vela = saldos.EnumerateArray().Single(s => s.GetProperty("codigoPeca").GetString() == "VELA-IGN-01");
        Assert.Equal(0, vela.GetProperty("quantidadeDisponivel").GetInt32());
    }

    [Fact]
    public async Task Saldos_FilialInexistente_Retorna404()
        => Assert.Equal(HttpStatusCode.NotFound,
            (await _factory.Funcionario("Atendente").GetAsync($"/operacoes/estoque/filiais/{Guid.NewGuid()}/saldos")).StatusCode);

    [Fact]
    public async Task EntradaEAjuste_AlteramSaldoERegistramMovimentacoes()
    {
        var http = _factory.Funcionario("Admin");
        var vela = await PecaAsync(http, "VELA-IGN-01");

        var entrada = await http.PostAsJsonAsync("/operacoes/estoque/entradas", new { filialId = Filial, pecaId = vela, quantidade = 12, motivo = "Compra NF 4521" });
        Assert.Equal(12, (await entrada.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quantidadeDisponivel").GetInt32());

        var ajuste = await http.PostAsJsonAsync("/operacoes/estoque/ajustes", new { filialId = Filial, pecaId = vela, quantidadeContada = 10, motivo = "Inventário mensal" });
        Assert.Equal(10, (await ajuste.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quantidadeDisponivel").GetInt32());

        var movs = await http.GetFromJsonAsync<JsonElement>($"/operacoes/estoque/filiais/{Filial}/movimentacoes?pecaId={vela}");
        Assert.Equal(["Ajuste", "Entrada"], movs.EnumerateArray().Select(m => m.GetProperty("tipo").GetString()));
        Assert.Equal(-2, movs[0].GetProperty("quantidade").GetInt32());
    }

    [Fact]
    public async Task Entrada_QuantidadeInvalida_Retorna400()
    {
        var resposta = await _factory.Funcionario("Admin").PostAsJsonAsync("/operacoes/estoque/entradas",
            new { filialId = Filial, pecaId = Guid.NewGuid(), quantidade = 0, motivo = "" });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Disponibilidade_InformaSemReservar()
    {
        var http = _factory.Funcionario("Atendente");
        var oleo = await PecaAsync(http, "OLEO-5W30-1L");
        var vela = await PecaAsync(http, "VELA-IGN-01");

        var resposta = await http.PostAsJsonAsync("/operacoes/estoque/disponibilidade",
            new { filialId = Filial, itens = new[] { new { pecaId = oleo, quantidade = 4 }, new { pecaId = vela, quantidade = 1 } } });

        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(corpo.GetProperty("atende").GetBoolean());
        Assert.True(corpo.GetProperty("itens").EnumerateArray().Single(i => i.GetProperty("pecaId").GetGuid() == oleo).GetProperty("atende").GetBoolean());

        var saldos = await http.GetFromJsonAsync<JsonElement>($"/operacoes/estoque/filiais/{Filial}/saldos");
        Assert.All(saldos.EnumerateArray(), s => Assert.Equal(0, s.GetProperty("quantidadeReservada").GetInt32()));
    }

    // ── Operação ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthEReadiness()
    {
        var http = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/operacoes/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/operacoes/ready")).StatusCode);
    }

    [Fact]
    public async Task Resposta_DevolveCorrelationIdRecebido()
    {
        var correlationId = Guid.NewGuid().ToString();
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Correlation-Id", correlationId);

        var resposta = await http.GetAsync("/operacoes/health");

        Assert.Equal(correlationId, resposta.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Swagger_PublicaRotasSemReservaManual()
    {
        var swagger = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/operacoes/swagger/v1/swagger.json");

        var rotas = swagger.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("/operacoes/pecas", rotas);
        Assert.Contains("/operacoes/servicos", rotas);
        Assert.Contains("/operacoes/estoque/filiais/{filialId}/saldos", rotas);
        Assert.Contains("/operacoes/estoque/disponibilidade", rotas);
        // Reserva, consumo e liberação são só da Saga (ADR-017).
        Assert.DoesNotContain(rotas, r => r.Contains("reserva", StringComparison.OrdinalIgnoreCase));

        // Evidência do CARD-38a: com EXPORTAR_OPENAPI definido, grava o OpenAPI gerado nesse caminho (docs/openapi/operacoes-v1.json).
        if (Environment.GetEnvironmentVariable("EXPORTAR_OPENAPI") is { Length: > 0 } caminho)
            await File.WriteAllTextAsync(caminho, JsonSerializer.Serialize(swagger, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private static async Task<Guid> PecaAsync(HttpClient http, string codigo)
        => (await http.GetFromJsonAsync<JsonElement>("/operacoes/pecas")).EnumerateArray()
            .Single(p => p.GetProperty("codigo").GetString() == codigo).GetProperty("id").GetGuid();
}
