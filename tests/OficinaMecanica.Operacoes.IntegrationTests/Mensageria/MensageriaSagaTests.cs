using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OficinaMecanica.Operacoes.Domain.Estoque;
using OficinaMecanica.Operacoes.Infrastructure;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Seed;
using OficinaMecanica.Operacoes.IntegrationTests.Fixtures;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace OficinaMecanica.Operacoes.IntegrationTests.Mensageria;

// Fluxo real pelo RabbitMQ e PostgreSQL: comando do OS -> inbox + efeito + outbox -> evento publicado (CARD-38a).
[Collection(PostgresCollection.Nome)]
public class MensageriaSagaTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Usuario = "operacoes-teste";
    private const string Senha = "operacoes-teste-senha";
    private const string PedidoReserva = "saga-os.inventory-reservation-requested.v1";
    private const string PedidoLiberacao = "saga-os.inventory-release-requested.v1";
    private const string Reservado = "saga-os.inventory-reserved.v1";
    private const string Recusado = "saga-os.inventory-reservation-rejected.v1";
    private const string Liberado = "saga-os.inventory-released.v1";

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
    private Guid _oleo, _vela;

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
            ["RabbitMq:IntervaloOutboxMs"] = "100"
        });
        builder.Services.AddInfrastructure(builder.Configuration).AddMensageria(builder.Configuration);
        _host = builder.Build();
        await _host.StartAsync();

        var estado = _host.Services.GetRequiredService<EstadoConsumidorSaga>();
        await Aguardar(() => Task.FromResult(estado.Consumindo));

        _conexao = await new ConnectionFactory { Uri = uri, UserName = Usuario, Password = Senha }.CreateConnectionAsync();
        _canal = await _conexao.CreateChannelAsync();
        foreach (var evento in new[] { Reservado, Recusado, Liberado })
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

    private static string FilaTeste(string exchange) => $"teste.{exchange}";

    private static JsonObject Pedido(string canal, params (string Campo, JsonNode Valor)[] payload)
    {
        var tipo = canal == PedidoReserva ? "InventoryReservationRequested.v1" : "InventoryReleaseRequested.v1";
        var osId = Guid.NewGuid();
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
