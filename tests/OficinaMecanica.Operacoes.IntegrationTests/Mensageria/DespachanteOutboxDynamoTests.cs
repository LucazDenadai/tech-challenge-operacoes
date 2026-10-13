using System.Globalization;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Messaging;
using OficinaMecanica.Operacoes.IntegrationTests.Fixtures;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace OficinaMecanica.Operacoes.IntegrationTests.Mensageria;

// Duas réplicas despachando a outbox do DynamoDB ao mesmo tempo (CARD-41b): a reserva condicional por item faz
// cada evento ser publicado uma vez.
[Collection(DynamoDbCollection.Nome)]
public class DespachanteOutboxDynamoTests(DynamoDbFixture dynamo) : IAsyncLifetime
{
    private const string Canal = "saga-os.execution-started.v1";
    private const string Fila = "teste.despachante-dynamo";

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.13-alpine")
        .WithUsername("operacoes-teste").WithPassword("operacoes-teste-senha").Build();

    public Task InitializeAsync() => _rabbit.StartAsync();

    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();

    [Fact]
    public async Task DuasReplicasAoMesmoTempo_CadaEventoPublicadoUmaVez()
    {
        var (cliente, opcoesDynamo) = await dynamo.NovaTabelaAsync();
        for (var i = 0; i < 20; i++)
        {
            var id = Guid.NewGuid();
            var criadaEm = DateTime.UtcNow.AddMilliseconds(i).ToString("O", CultureInfo.InvariantCulture);
            await cliente.PutItemAsync(new PutItemRequest
            {
                TableName = opcoesDynamo.Value.Tabela,
                Item = new()
                {
                    [ChavesDynamo.Pk] = new AttributeValue { S = ChavesDynamo.Outbox(id) },
                    [ChavesDynamo.Sk] = new AttributeValue { S = "OUTBOX" },
                    [ChavesDynamo.Gsi1Pk] = new AttributeValue { S = ChavesDynamo.OutboxPendente },
                    [ChavesDynamo.Gsi1Sk] = new AttributeValue { S = $"{criadaEm}#{id}" },
                    ["MessageId"] = new AttributeValue { S = id.ToString() },
                    ["MessageType"] = new AttributeValue { S = "ExecutionStarted.v1" },
                    ["Canal"] = new AttributeValue { S = Canal },
                    ["CorrelationId"] = new AttributeValue { S = Guid.NewGuid().ToString() },
                    ["Payload"] = new AttributeValue { S = "{}" },
                    ["CriadaEm"] = new AttributeValue { S = criadaEm }
                }
            });
        }

        var uri = new Uri(_rabbit.GetConnectionString());
        var opcoes = Options.Create(new RabbitMqOptions
        {
            Enabled = true, Host = uri.Host, Port = uri.Port, Username = "operacoes-teste", Password = "operacoes-teste-senha"
        });
        await using var conexao = await opcoes.Value.CriarFabrica("teste").CreateConnectionAsync();
        await using var canal = await conexao.CreateChannelAsync();
        await canal.ExchangeDeclareAsync(Canal, ExchangeType.Fanout, durable: true, autoDelete: false);
        await canal.QueueDeclareAsync(Fila, durable: false, exclusive: false, autoDelete: false);
        await canal.QueueBindAsync(Fila, Canal, string.Empty);

        DespachanteOutboxDynamoHostedService Replica() => new(cliente, opcoesDynamo, new PublicadorRabbitMq(opcoes), opcoes,
            NullLogger<DespachanteOutboxDynamoHostedService>.Instance);

        // Cada réplica despacha até a lista de pendentes esvaziar; as duas rodam em paralelo.
        static async Task Esgotar(DespachanteOutboxDynamoHostedService despachante)
        {
            while (await despachante.DespacharAsync(CancellationToken.None) > 0) { }
        }
        await Task.WhenAll(Esgotar(Replica()), Esgotar(Replica()));

        var publicadas = new List<string>();
        BasicGetResult? resultado;
        while ((resultado = await canal.BasicGetAsync(Fila, autoAck: true)) is not null)
            publicadas.Add(resultado.BasicProperties.MessageId!);
        Assert.Equal(20, publicadas.Count);
        Assert.Equal(20, publicadas.Distinct().Count());
    }
}
