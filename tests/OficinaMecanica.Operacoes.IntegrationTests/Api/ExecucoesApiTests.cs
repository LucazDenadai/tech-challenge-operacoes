using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Operacoes.Application.Contratos.Saga;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Application.UseCases.Estoque;
using OficinaMecanica.Operacoes.Application.UseCases.Execucoes;
using OficinaMecanica.Operacoes.Domain.Estoque;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Seed;
using OficinaMecanica.Operacoes.IntegrationTests.Fixtures;

namespace OficinaMecanica.Operacoes.IntegrationTests.Api;

// API de execução do CARD-38b contra a API real, PostgreSQL e DynamoDB Local. Os comandos da Saga entram
// pelos casos de uso (sem RabbitMQ aqui; o fluxo pelo broker está em MensageriaSagaTests).
[Collection(PostgresCollection.Nome)]
public class ExecucoesApiTests : IAsyncLifetime
{
    private static readonly Guid Filial = SeedDemonstracao.FilialDemoId;

    private readonly OperacoesApiFactory _factory;

    public ExecucoesApiTests(PostgresFixture fixture, DynamoDbFixture dynamo) => _factory = new OperacoesApiFactory(fixture.NovoBancoVazio(), dynamo);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task CicloPelaApi_DiagnosticoFilaReparoEConclusao()
    {
        var osId = Guid.NewGuid();
        var mecanico = _factory.Funcionario("Mecanico");
        var oleo = await PecaAsync(mecanico, "OLEO-5W30-1L");
        var servico = await ServicoAsync(mecanico, "Troca de óleo e filtro");
        var execucaoId = await AbrirExecucaoAsync(osId);

        var fila = await mecanico.GetFromJsonAsync<JsonElement>($"/operacoes/execucoes?filialId={Filial}&status=EmDiagnostico");
        Assert.Contains(fila.EnumerateArray(), e => e.GetProperty("id").GetGuid() == execucaoId);

        var diagnostico = await mecanico.PostAsJsonAsync($"/operacoes/execucoes/{execucaoId}/diagnostico", new
        {
            itens = new object[]
            {
                new { tipo = "Peca", itemId = oleo, quantidade = 4 },
                new { tipo = "Servico", itemId = servico, quantidade = 1 }
            }
        });
        Assert.Equal(HttpStatusCode.OK, diagnostico.StatusCode);
        var corpo = await diagnostico.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Diagnosticada", corpo.GetProperty("status").GetString());
        Assert.Contains(corpo.GetProperty("itens").EnumerateArray(), i => i.GetProperty("precoUnitario").GetDecimal() == 39.90m);

        await IniciarPelaSagaAsync(osId, execucaoId, oleo, 4);

        Assert.Equal(HttpStatusCode.OK, (await mecanico.PostAsync($"/operacoes/execucoes/{execucaoId}/reparo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mecanico.PostAsJsonAsync($"/operacoes/execucoes/{execucaoId}/etapas", new { descricao = "Óleo drenado" })).StatusCode);
        var conclusao = await mecanico.PostAsJsonAsync($"/operacoes/execucoes/{execucaoId}/conclusao",
            new { pecasConsumidas = new[] { new { pecaId = oleo, quantidade = 4 } } });
        Assert.Equal(HttpStatusCode.OK, conclusao.StatusCode);

        var final = await mecanico.GetFromJsonAsync<JsonElement>($"/operacoes/execucoes/os/{osId}");
        Assert.Equal("Concluida", final.GetProperty("status").GetString());
        Assert.Equal(
            ["EmDiagnostico", "Diagnosticada", "NaFila", "EmReparo", "Concluida"],
            final.GetProperty("historico").EnumerateArray().Select(h => h.GetProperty("para").GetString()));
        Assert.Equal("mecanico@oficina.example", final.GetProperty("historico")[1].GetProperty("responsavel").GetString());

        var saldo = (await mecanico.GetFromJsonAsync<JsonElement>($"/operacoes/estoque/filiais/{Filial}/saldos"))
            .EnumerateArray().Single(s => s.GetProperty("pecaId").GetGuid() == oleo);
        Assert.Equal(56, saldo.GetProperty("quantidadeDisponivel").GetInt32());
        Assert.Equal(0, saldo.GetProperty("quantidadeReservada").GetInt32());
    }

    [Fact]
    public async Task Atendente_ConsultaMasNaoDiagnostica()
    {
        var execucaoId = await AbrirExecucaoAsync(Guid.NewGuid());
        var atendente = _factory.Funcionario("Atendente");

        Assert.Equal(HttpStatusCode.OK, (await atendente.GetAsync($"/operacoes/execucoes/{execucaoId}")).StatusCode);
        var resposta = await atendente.PostAsJsonAsync($"/operacoes/execucoes/{execucaoId}/diagnostico/rejeicao", new { motivo = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    [Fact]
    public async Task SemTokenOuTokenDeCliente_Recusado()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/operacoes/execucoes/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClienteFinal().GetAsync($"/operacoes/execucoes/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task TransicaoInvalida_Retorna422()
    {
        var execucaoId = await AbrirExecucaoAsync(Guid.NewGuid());

        var resposta = await _factory.Funcionario("Mecanico").PostAsync($"/operacoes/execucoes/{execucaoId}/reparo", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
    }

    [Fact]
    public async Task DiagnosticoInvalido_400OuItemInexistente404()
    {
        var execucaoId = await AbrirExecucaoAsync(Guid.NewGuid());
        var mecanico = _factory.Funcionario("Mecanico");

        var vazio = await mecanico.PostAsJsonAsync($"/operacoes/execucoes/{execucaoId}/diagnostico", new { itens = Array.Empty<object>() });
        var inexistente = await mecanico.PostAsJsonAsync($"/operacoes/execucoes/{execucaoId}/diagnostico",
            new { itens = new[] { new { tipo = "Peca", itemId = Guid.NewGuid(), quantidade = 1 } } });

        Assert.Equal(HttpStatusCode.BadRequest, vazio.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
    }

    [Fact]
    public async Task RejeicaoDoDiagnostico_EncerraComMotivo()
    {
        var execucaoId = await AbrirExecucaoAsync(Guid.NewGuid());

        var resposta = await _factory.Funcionario("Admin").PostAsJsonAsync($"/operacoes/execucoes/{execucaoId}/diagnostico/rejeicao",
            new { motivo = "Veículo sem conserto viável" });

        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DiagnosticoRejeitado", corpo.GetProperty("status").GetString());
        Assert.Equal("Veículo sem conserto viável", corpo.GetProperty("motivo").GetString());
    }

    [Fact]
    public async Task Fila_ExigeFilialEExecucaoInexistente404()
    {
        var http = _factory.Funcionario("Atendente");

        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/operacoes/execucoes?status=NaFila")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/operacoes/execucoes/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/operacoes/execucoes/os/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Readiness_IncluiDynamoDb()
        => Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync("/operacoes/ready")).StatusCode);

    // A execução nasce do DiagnosisRequested da Saga.
    private async Task<Guid> AbrirExecucaoAsync(Guid osId)
    {
        using var escopo = _factory.Services.CreateScope();
        var comando = new DiagnosisRequested
        {
            MessageId = Guid.NewGuid(), MessageType = "DiagnosisRequested.v1", SchemaVersion = 1, Producer = "os",
            OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = Guid.NewGuid(), OsId = osId, FilialId = Filial,
            IdempotencyKey = $"diagnostico:{osId}", VeiculoId = Guid.NewGuid()
        };
        await escopo.ServiceProvider.GetRequiredService<ComandosExecucaoUseCase>()
            .ProcessarAsync(new MensagemRecebida(comando, "saga-os.diagnosis-requested.v1", "{}"));
        return (await escopo.ServiceProvider.GetRequiredService<ConsultarExecucoesUseCase>().ObterPorOsAsync(osId)).Id;
    }

    // Reserva (CARD-38a) e ExecutionStartRequested, como a Saga faria após o pagamento.
    private async Task IniciarPelaSagaAsync(Guid osId, Guid execucaoId, Guid pecaId, int quantidade)
    {
        using var escopo = _factory.Services.CreateScope();
        var servicos = escopo.ServiceProvider;
        var reserva = (ReservaConfirmada)await servicos.GetRequiredService<ReservarEstoqueUseCase>()
            .ExecutarAsync(new ReservarEstoqueCommand(osId, Filial, Guid.NewGuid(), $"reserva:{osId}", [new ItemQuantidade(pecaId, quantidade)]));
        await servicos.GetRequiredService<IUnidadeDeTrabalho>().SalvarAsync();

        var inicio = new ExecutionStartRequested
        {
            MessageId = Guid.NewGuid(), MessageType = "ExecutionStartRequested.v1", SchemaVersion = 1, Producer = "os",
            OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = Guid.NewGuid(), OsId = osId, FilialId = Filial,
            IdempotencyKey = $"inicio:{osId}", ExecutionId = execucaoId, ReservationId = reserva.ReservaId
        };
        await servicos.GetRequiredService<ComandosExecucaoUseCase>()
            .ProcessarAsync(new MensagemRecebida(inicio, "saga-os.execution-start-requested.v1", "{}"));
    }

    private static async Task<Guid> PecaAsync(HttpClient http, string codigo)
        => (await http.GetFromJsonAsync<JsonElement>("/operacoes/pecas")).EnumerateArray()
            .Single(p => p.GetProperty("codigo").GetString() == codigo).GetProperty("id").GetGuid();

    private static async Task<Guid> ServicoAsync(HttpClient http, string nome)
        => (await http.GetFromJsonAsync<JsonElement>("/operacoes/servicos")).EnumerateArray()
            .Single(s => s.GetProperty("nome").GetString() == nome).GetProperty("id").GetGuid();
}
