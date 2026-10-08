using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OficinaMecanica.Operacoes.Application.UseCases.Execucoes;
using OficinaMecanica.Operacoes.Domain.Estoque;
using OficinaMecanica.Operacoes.Domain.Execucoes;
using OficinaMecanica.Operacoes.Infrastructure;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Seed;
using OficinaMecanica.Operacoes.IntegrationTests.Fixtures;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace OficinaMecanica.Operacoes.IntegrationTests.Mensageria;

// Fluxo real pelo RabbitMQ, PostgreSQL e DynamoDB Local: comando do OS -> inbox + efeito + outbox -> evento publicado
// (CARD-38a para o Estoque, CARD-38b para a Execução).
[Collection(PostgresCollection.Nome)]
public class MensageriaSagaTests(PostgresFixture postgres, DynamoDbFixture dynamo) : IAsyncLifetime
{
    private const string Usuario = "operacoes-teste";
    private const string Senha = "operacoes-teste-senha";
    private const string PedidoReserva = "saga-os.inventory-reservation-requested.v1";
    private const string PedidoLiberacao = "saga-os.inventory-release-requested.v1";
    private const string Reservado = "saga-os.inventory-reserved.v1";
    private const string Recusado = "saga-os.inventory-reservation-rejected.v1";
    private const string Liberado = "saga-os.inventory-released.v1";
    private const string PedidoDiagnostico = "saga-os.diagnosis-requested.v1";
    private const string PedidoInicio = "saga-os.execution-start-requested.v1";
    private const string Diagnosticado = "saga-os.diagnosis-completed.v1";
    private const string Iniciada = "saga-os.execution-started.v1";
    private const string InicioRecusado = "saga-os.execution-start-rejected.v1";
    private const string Concluida = "saga-os.execution-completed.v1";

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.13-alpine")
        .WithUsername(Usuario)
        .WithPassword(Senha)
        .Build();

    // Sem OpenTelemetry no host de teste: o listener faz a fonte de mensageria criar spans de verdade.
    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = fonte => fonte.Name == TelemetriaMensageria.NomeFonte,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
    };

    private IHost _host = null!;
    private string _conexaoBanco = null!;
    private IConnection _conexao = null!;
    private IChannel _canal = null!;
    private Guid _oleo, _vela, _servico;

    public async Task InitializeAsync()
    {
        ActivitySource.AddActivityListener(_listener);
        await _rabbit.StartAsync();
        var uri = new Uri(_rabbit.GetConnectionString());

        _conexaoBanco = await postgres.NovoBancoMigradoAsync();
        await using (var db = PostgresFixture.CriarContexto(_conexaoBanco))
        {
            await SeedDemonstracao.ExecutarAsync(db);
            _oleo = (await db.Pecas.SingleAsync(p => p.Codigo == "OLEO-5W30-1L")).Id;
            _vela = (await db.Pecas.SingleAsync(p => p.Codigo == "VELA-IGN-01")).Id;
            _servico = (await db.Servicos.SingleAsync(s => s.Nome == "Troca de óleo e filtro")).Id;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _conexaoBanco,
            ["RabbitMq:Enabled"] = "true",
            ["RabbitMq:Host"] = uri.Host,
            ["RabbitMq:Port"] = uri.Port.ToString(),
            ["RabbitMq:Username"] = Usuario,
            ["RabbitMq:Password"] = Senha,
            ["RabbitMq:IntervaloOutboxMs"] = "100",
            ["DynamoDb:ServiceUrl"] = dynamo.Endpoint,
            ["DynamoDb:AccessKey"] = DynamoDbFixture.AccessKey,
            ["DynamoDb:SecretKey"] = DynamoDbFixture.SecretKey,
            ["DynamoDb:Tabela"] = $"execucoes-{Guid.NewGuid():N}",
            ["DynamoDb:CriarTabela"] = "true"
        });
        builder.Services.AddInfrastructure(builder.Configuration).AddMensageria(builder.Configuration);
        _host = builder.Build();
        await _host.StartAsync();

        var estado = _host.Services.GetRequiredService<EstadoConsumidorSaga>();
        await Aguardar(() => Task.FromResult(estado.Consumindo));

        _conexao = await new ConnectionFactory { Uri = uri, UserName = Usuario, Password = Senha }.CreateConnectionAsync();
        _canal = await _conexao.CreateChannelAsync();
        foreach (var evento in new[] { Reservado, Recusado, Liberado, Diagnosticado, Iniciada, InicioRecusado, Concluida })
        {
            await _canal.ExchangeDeclareAsync(evento, ExchangeType.Fanout, durable: true, autoDelete: false);
            await _canal.QueueDeclareAsync(FilaTeste(evento), durable: false, exclusive: false, autoDelete: false);
            await _canal.QueueBindAsync(FilaTeste(evento), evento, string.Empty);
        }
    }

    public async Task DisposeAsync()
    {
        await _canal.DisposeAsync();
        await _conexao.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        await _rabbit.DisposeAsync();
        _listener.Dispose();
    }

    [Fact]
    public async Task PedidoDeReserva_PublicaInventoryReservedCorrelacionadoNoMesmoTrace()
    {
        var pedido = Pedido(PedidoReserva, ("items", Itens(_oleo, 4)));
        var traceId = ActivityTraceId.CreateRandom();

        await PublicarAsync(PedidoReserva, pedido, $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");

        var (evento, propriedades) = await ReceberAsync(FilaTeste(Reservado));
        Assert.Equal("InventoryReserved.v1", evento["messageType"]!.GetValue<string>());
        Assert.Equal(pedido["messageId"]!.GetValue<string>(), evento["causationId"]!.GetValue<string>());
        Assert.Equal(pedido["correlationId"]!.GetValue<string>(), evento["correlationId"]!.GetValue<string>());
        Assert.Equal(evento["messageId"]!.GetValue<string>(), propriedades.MessageId);
        Assert.Equal(4, evento["items"]![0]!["quantity"]!.GetValue<int>());

        var traceParent = Encoding.UTF8.GetString((byte[])propriedades.Headers!["traceparent"]!);
        Assert.Contains(traceId.ToString(), traceParent);

        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        var saldo = await db.Saldos.SingleAsync(s => s.PecaId == _oleo);
        Assert.Equal(4, saldo.QuantidadeReservada);
        Assert.Equal(1, await db.Inbox.CountAsync());
        Assert.NotNull((await db.Outbox.SingleAsync()).PublicadaEmUtc);
    }

    [Fact]
    public async Task PedidoReentregue_UmaReservaEUmEvento()
    {
        var pedido = Pedido(PedidoReserva, ("items", Itens(_oleo, 2)));

        await PublicarAsync(PedidoReserva, pedido);
        await PublicarAsync(PedidoReserva, pedido);

        await ReceberAsync(FilaTeste(Reservado));
        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        await Aguardar(async () => await _canal.MessageCountAsync($"operacoes.{PedidoReserva}") == 0);
        await Task.Delay(500);

        Assert.Equal(0u, await _canal.MessageCountAsync(FilaTeste(Reservado)));
        Assert.Equal(1, await db.Reservas.CountAsync());
        Assert.Equal(2, (await db.Saldos.SingleAsync(s => s.PecaId == _oleo)).QuantidadeReservada);
    }

    [Fact]
    public async Task PecaSemSaldo_PublicaRecusaSemAlterarSaldo()
    {
        await PublicarAsync(PedidoReserva, Pedido(PedidoReserva, ("items", Itens(_vela, 1))));

        var (evento, _) = await ReceberAsync(FilaTeste(Recusado));
        Assert.Equal(_vela.ToString(), evento["unavailablePecaIds"]![0]!.GetValue<string>());

        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        Assert.Equal(0, await db.Reservas.CountAsync());
    }

    [Fact]
    public async Task ReservaELiberacao_DevolveSaldoEPublicaInventoryReleased()
    {
        var pedido = Pedido(PedidoReserva, ("items", Itens(_oleo, 3)));
        await PublicarAsync(PedidoReserva, pedido);
        var (reservado, _) = await ReceberAsync(FilaTeste(Reservado));

        var liberacao = Pedido(PedidoLiberacao, ("reason", "Pagamento recusado"), ("reservationId", reservado["reservationId"]!.GetValue<string>()));
        liberacao["correlationId"] = pedido["correlationId"]!.GetValue<string>();
        await PublicarAsync(PedidoLiberacao, liberacao);

        var (liberado, _) = await ReceberAsync(FilaTeste(Liberado));
        Assert.Equal(3, liberado["releasedItems"]![0]!["quantity"]!.GetValue<int>());

        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        var saldo = await db.Saldos.SingleAsync(s => s.PecaId == _oleo);
        Assert.Equal(60, saldo.QuantidadeDisponivel);
        Assert.Equal(0, saldo.QuantidadeReservada);
        var correlacao = Guid.Parse(pedido["correlationId"]!.GetValue<string>());
        Assert.Equal(2, await db.Movimentacoes.CountAsync(m => m.CorrelationId == correlacao));
        Assert.Contains(await db.Movimentacoes.Where(m => m.CorrelationId == correlacao).Select(m => m.Tipo).ToListAsync(),
            t => t == TipoMovimentacao.Liberacao);
    }

    [Fact]
    public async Task ComandoSemIdempotencyKey_VaiParaDlqComMotivo()
    {
        var pedido = Pedido(PedidoReserva, ("items", Itens(_oleo, 1)));
        pedido.Remove("idempotencyKey");

        await PublicarAsync(PedidoReserva, pedido);

        var (_, propriedades) = await ReceberAsync($"operacoes.{PedidoReserva}.dlq");
        Assert.Contains("idempotencyKey", Encoding.UTF8.GetString((byte[])propriedades.Headers!["x-motivo"]!));
        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        Assert.Equal(0, await db.Inbox.CountAsync());
        Assert.Equal(0, await db.Outbox.CountAsync());
    }

    [Fact]
    public async Task LiberacaoDeReservaInexistente_VaiParaDlqSemRegistrarNaInbox()
    {
        await PublicarAsync(PedidoLiberacao, Pedido(PedidoLiberacao, ("reason", "Compensação"), ("reservationId", Guid.NewGuid().ToString())));

        var (_, propriedades) = await ReceberAsync($"operacoes.{PedidoLiberacao}.dlq");
        Assert.Contains("Reserva", Encoding.UTF8.GetString((byte[])propriedades.Headers!["x-motivo"]!));
        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        Assert.Equal(0, await db.Inbox.CountAsync());
    }

    [Fact]
    public async Task FluxoDaExecucao_DiagnosticoReservaInicioEConclusao()
    {
        var osId = Guid.NewGuid();
        var correlacao = Guid.NewGuid().ToString();
        const string Mecanico = "mecanico@oficina.example";

        // 1. OS pede o diagnóstico: a execução nasce no DynamoDB, sem evento até o técnico registrar.
        var pedidoDiagnostico = PedidoDaOs(PedidoDiagnostico, osId, ("veiculoId", Guid.NewGuid().ToString()));
        pedidoDiagnostico["correlationId"] = correlacao;
        await PublicarAsync(PedidoDiagnostico, pedidoDiagnostico);
        ExecucaoResponse? execucao = null;
        await Aguardar(async () => (execucao = await ExecucaoDaOsAsync(osId)) is not null);

        // 2. Técnico registra o diagnóstico: DiagnosisCompleted com o preço do catálogo.
        await UsarAcoesAsync(a => a.RegistrarDiagnosticoAsync(execucao!.Id, new RegistrarDiagnosticoRequest
        {
            Itens =
            [
                new() { Tipo = TipoItemDiagnostico.Peca, ItemId = _oleo, Quantidade = 4 },
                new() { Tipo = TipoItemDiagnostico.Servico, ItemId = _servico, Quantidade = 1 }
            ]
        }, Mecanico));
        var (diagnostico, _) = await ReceberAsync(FilaTeste(Diagnosticado));
        Assert.Equal(execucao!.Id.ToString(), diagnostico["executionId"]!.GetValue<string>());
        Assert.Equal(pedidoDiagnostico["messageId"]!.GetValue<string>(), diagnostico["causationId"]!.GetValue<string>());
        Assert.Contains(diagnostico["items"]!.AsArray(), i => i!["unitPrice"]!.GetValue<decimal>() == 39.90m);

        // 3. Reserva das peças (CARD-38a).
        var pedidoReserva = PedidoDaOs(PedidoReserva, osId, ("items", Itens(_oleo, 4)));
        pedidoReserva["correlationId"] = correlacao;
        await PublicarAsync(PedidoReserva, pedidoReserva);
        var (reservado, _) = await ReceberAsync(FilaTeste(Reservado));

        // 4. Pagamento aprovado, OS pede o início: entra na fila, no mesmo trace do comando.
        var traceId = ActivityTraceId.CreateRandom();
        var pedidoInicio = PedidoDaOs(PedidoInicio, osId,
            ("executionId", execucao.Id.ToString()), ("reservationId", reservado["reservationId"]!.GetValue<string>()));
        pedidoInicio["correlationId"] = correlacao;
        await PublicarAsync(PedidoInicio, pedidoInicio, $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        var (iniciada, propriedadesInicio) = await ReceberAsync(FilaTeste(Iniciada));
        Assert.Equal(pedidoInicio["messageId"]!.GetValue<string>(), iniciada["causationId"]!.GetValue<string>());
        Assert.Contains(traceId.ToString(), Encoding.UTF8.GetString((byte[])propriedadesInicio.Headers!["traceparent"]!));

        // 5. Reparo e conclusão com 3 das 4 unidades: ExecutionCompleted e a sobra volta ao estoque.
        await UsarAcoesAsync(a => a.IniciarReparoAsync(execucao.Id, Mecanico));
        await UsarAcoesAsync(a => a.RegistrarEtapaAsync(execucao.Id, "Óleo drenado", Mecanico));
        await UsarAcoesAsync(a => a.ConcluirAsync(execucao.Id, [new() { PecaId = _oleo, Quantidade = 3 }], Mecanico));
        var (concluida, _) = await ReceberAsync(FilaTeste(Concluida));
        Assert.Equal(3, concluida["consumedItems"]![0]!["quantity"]!.GetValue<int>());
        Assert.Equal(correlacao, concluida["correlationId"]!.GetValue<string>());

        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        var saldo = await db.Saldos.SingleAsync(s => s.PecaId == _oleo);
        Assert.Equal(57, saldo.QuantidadeDisponivel);
        Assert.Equal(0, saldo.QuantidadeReservada);
        var final = (await ExecucaoDaOsAsync(osId))!;
        Assert.Equal(StatusExecucao.Concluida, final.Status);
        Assert.Single(final.Etapas);
    }

    [Fact]
    public async Task InicioSemDiagnostico_PublicaExecutionStartRejected()
    {
        var osId = Guid.NewGuid();
        await PublicarAsync(PedidoDiagnostico, PedidoDaOs(PedidoDiagnostico, osId, ("veiculoId", Guid.NewGuid().ToString())));
        ExecucaoResponse? execucao = null;
        await Aguardar(async () => (execucao = await ExecucaoDaOsAsync(osId)) is not null);

        await PublicarAsync(PedidoInicio, PedidoDaOs(PedidoInicio, osId,
            ("executionId", execucao!.Id.ToString()), ("reservationId", Guid.NewGuid().ToString())));

        var (recusa, _) = await ReceberAsync(FilaTeste(InicioRecusado));
        Assert.Contains("EmDiagnostico", recusa["reason"]!.GetValue<string>());
        Assert.Equal(StatusExecucao.EmDiagnostico, (await ExecucaoDaOsAsync(osId))!.Status);
    }

    private async Task<ExecucaoResponse?> ExecucaoDaOsAsync(Guid osId)
    {
        using var escopo = _host.Services.CreateScope();
        try
        {
            return await escopo.ServiceProvider.GetRequiredService<ConsultarExecucoesUseCase>().ObterPorOsAsync(osId);
        }
        catch (OficinaMecanica.Operacoes.Application.Exceptions.NotFoundException)
        {
            return null;
        }
    }

    private async Task UsarAcoesAsync(Func<AcoesExecucaoUseCase, Task> acao)
    {
        using var escopo = _host.Services.CreateScope();
        await acao(escopo.ServiceProvider.GetRequiredService<AcoesExecucaoUseCase>());
    }

    private static string FilaTeste(string exchange) => $"teste.{exchange}";

    private static JsonObject Pedido(string canal, params (string Campo, JsonNode Valor)[] payload)
        => PedidoDaOs(canal, Guid.NewGuid(), payload);

    private static JsonObject PedidoDaOs(string canal, Guid osId, params (string Campo, JsonNode Valor)[] payload)
    {
        var tipo = canal switch
        {
            PedidoReserva => "InventoryReservationRequested.v1",
            PedidoLiberacao => "InventoryReleaseRequested.v1",
            PedidoDiagnostico => "DiagnosisRequested.v1",
            _ => "ExecutionStartRequested.v1"
        };
        var mensagem = new JsonObject
        {
            ["messageId"] = Guid.NewGuid().ToString(),
            ["messageType"] = tipo,
            ["schemaVersion"] = 1,
            ["producer"] = "os",
            ["occurredAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["correlationId"] = Guid.NewGuid().ToString(),
            ["osId"] = osId.ToString(),
            ["filialId"] = SeedDemonstracao.FilialDemoId.ToString(),
            ["idempotencyKey"] = $"{tipo}:{osId}"
        };
        foreach (var (campo, valor) in payload)
            mensagem[campo] = valor;
        return mensagem;
    }

    private static JsonArray Itens(Guid pecaId, int quantidade)
        => new(new JsonObject { ["pecaId"] = pecaId.ToString(), ["quantity"] = quantidade });

    private async Task PublicarAsync(string exchange, JsonObject mensagem, string? traceParent = null)
    {
        var propriedades = new BasicProperties { ContentType = "application/json", Persistent = true };
        if (traceParent is not null)
            propriedades.Headers = new Dictionary<string, object?> { ["traceparent"] = traceParent };

        await _canal.BasicPublishAsync(exchange, string.Empty, mandatory: false, propriedades, Encoding.UTF8.GetBytes(mensagem.ToJsonString()));
    }

    private async Task<(JsonObject Corpo, IReadOnlyBasicProperties Propriedades)> ReceberAsync(string fila)
    {
        BasicGetResult? resultado = null;
        await Aguardar(async () => (resultado = await _canal.BasicGetAsync(fila, autoAck: true)) is not null);
        return (JsonNode.Parse(resultado!.Body.Span)!.AsObject(), resultado.BasicProperties);
    }

    private static async Task Aguardar(Func<Task<bool>> condicao)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            if (await condicao()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Condição não atingida em 30 s.");
    }
}
