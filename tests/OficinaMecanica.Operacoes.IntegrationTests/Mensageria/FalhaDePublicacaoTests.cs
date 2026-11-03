using System.Globalization;
using Amazon.DynamoDBv2.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Messaging;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Mensageria;
using OficinaMecanica.Operacoes.IntegrationTests.Fixtures;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace OficinaMecanica.Operacoes.IntegrationTests.Mensageria;

// O broker recusa a publicação (exchange já existe com outro tipo): a outbox guarda a tentativa e o erro, e o evento continua pendente.
[Collection(PostgresCollection.Nome)]
public class FalhaDePublicacaoTests(PostgresFixture postgres, DynamoDbFixture dynamo) : IAsyncLifetime
{
    private const string CanalComOutroTipo = "saga-os.canal-com-outro-tipo.v1";

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.13-alpine")
        .WithUsername("operacoes-teste").WithPassword("operacoes-teste-senha").Build();

    public async Task InitializeAsync()
    {
        await _rabbit.StartAsync();
        // O publicador declara a exchange como fanout; com outro tipo já declarado, o broker recusa (PRECONDITION_FAILED).
        await using var conexao = await Opcoes().Value.CriarFabrica("teste").CreateConnectionAsync();
        await using var canal = await conexao.CreateChannelAsync();
        await canal.ExchangeDeclareAsync(CanalComOutroTipo, ExchangeType.Direct, durable: true, autoDelete: false);
    }

    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();

    private IOptions<RabbitMqOptions> Opcoes()
    {
        var uri = new Uri(_rabbit.GetConnectionString());
        return Options.Create(new RabbitMqOptions
        {
            Enabled = true, Host = uri.Host, Port = uri.Port, Username = "operacoes-teste", Password = "operacoes-teste-senha"
        });
    }

    [Fact]
    public async Task OutboxDoPostgres_BrokerRecusa_RegistraTentativaEMantemPendente()
    {
        var banco = postgres.NovoBancoVazio();
        var messageId = Guid.NewGuid();
        await using (var db = PostgresFixture.CriarContexto(banco))
        {
            await db.Database.MigrateAsync();
            db.Outbox.Add(new MensagemOutbox
            {
                MessageId = messageId, MessageType = "InventoryReserved.v1", Canal = CanalComOutroTipo, CorrelationId = Guid.NewGuid(),
                Payload = "{}", CriadaEmUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var opcoes = Opcoes();
        var servicos = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseNpgsql(banco)).BuildServiceProvider();
        var despachante = new DespachanteOutboxHostedService(servicos.GetRequiredService<IServiceScopeFactory>(),
            new PublicadorRabbitMq(opcoes), opcoes, NullLogger<DespachanteOutboxHostedService>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => despachante.DespacharAsync(CancellationToken.None));

        await using var leitura = PostgresFixture.CriarContexto(banco);
        var mensagem = await leitura.Outbox.SingleAsync(m => m.MessageId == messageId);
        Assert.Null(mensagem.PublicadaEmUtc);
        Assert.Equal(1, mensagem.Tentativas);
        Assert.False(string.IsNullOrEmpty(mensagem.UltimoErro));
    }

    [Fact]
    public async Task OutboxDoDynamo_BrokerRecusa_RegistraTentativaELiberaAReserva()
    {
        var (cliente, opcoesDynamo) = await dynamo.NovaTabelaAsync();
        var id = Guid.NewGuid();
        var criadaEm = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var chave = new Dictionary<string, AttributeValue>
        {
            [ChavesDynamo.Pk] = new() { S = ChavesDynamo.Outbox(id) },
            [ChavesDynamo.Sk] = new() { S = "OUTBOX" }
        };
        await cliente.PutItemAsync(new PutItemRequest
        {
            TableName = opcoesDynamo.Value.Tabela,
            Item = new(chave)
            {
                [ChavesDynamo.Gsi1Pk] = new AttributeValue { S = ChavesDynamo.OutboxPendente },
                [ChavesDynamo.Gsi1Sk] = new AttributeValue { S = $"{criadaEm}#{id}" },
                ["MessageId"] = new AttributeValue { S = id.ToString() },
                ["MessageType"] = new AttributeValue { S = "ExecutionStarted.v1" },
                ["Canal"] = new AttributeValue { S = CanalComOutroTipo },
                ["CorrelationId"] = new AttributeValue { S = Guid.NewGuid().ToString() },
                ["Payload"] = new AttributeValue { S = "{}" },
                ["CriadaEm"] = new AttributeValue { S = criadaEm }
            }
        });
        var opcoes = Opcoes();
        var despachante = new DespachanteOutboxDynamoHostedService(cliente, opcoesDynamo, new PublicadorRabbitMq(opcoes), opcoes,
            NullLogger<DespachanteOutboxDynamoHostedService>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => despachante.DespacharAsync(CancellationToken.None));

        var item = (await cliente.GetItemAsync(opcoesDynamo.Value.Tabela, chave)).Item;
        Assert.Equal("1", item["Tentativas"].N);
        Assert.False(string.IsNullOrEmpty(item["UltimoErro"].S));
        Assert.False(item.ContainsKey("ReservadoAte"));
        Assert.Equal(ChavesDynamo.OutboxPendente, item[ChavesDynamo.Gsi1Pk].S);
    }
}
