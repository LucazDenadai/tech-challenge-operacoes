using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;

namespace OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Messaging;

// Outbox do DynamoDB: eventos da Execução (ADR-016). Pendentes ficam no GSI1 (OUTBOX#PENDENTE);
// ao publicar, os atributos do índice são removidos e o item sai da lista de pendentes.
public class DespachanteOutboxDynamoHostedService(
    IAmazonDynamoDB dynamo,
    IOptions<DynamoDbOptions> dynamoOptions,
    PublicadorRabbitMq publicador,
    IOptions<RabbitMqOptions> options,
    ILogger<DespachanteOutboxDynamoHostedService> logger) : DespachanteOutboxBase(options, logger)
{
    private readonly string _tabela = dynamoOptions.Value.Tabela;

    public override async Task<int> DespacharAsync(CancellationToken ct)
    {
        var resposta = await dynamo.QueryAsync(new QueryRequest
        {
            TableName = _tabela,
            IndexName = ChavesDynamo.Gsi1,
            KeyConditionExpression = "GSI1PK = :pendente",
            ExpressionAttributeValues = new() { [":pendente"] = new AttributeValue { S = ChavesDynamo.OutboxPendente } },
            Limit = Lote
        }, ct);

        var pendentes = resposta.Items ?? [];
        foreach (var item in pendentes)
        {
            var chave = new Dictionary<string, AttributeValue> { [ChavesDynamo.Pk] = item[ChavesDynamo.Pk], [ChavesDynamo.Sk] = item[ChavesDynamo.Sk] };
            try
            {
                await publicador.PublicarAsync(new MensagemParaPublicar(
                    Guid.Parse(item["MessageId"].S), item["MessageType"].S, item["Canal"].S, Guid.Parse(item["CorrelationId"].S),
                    item["Payload"].S, item.TryGetValue("TraceParent", out var trace) ? trace.S : null), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await dynamo.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tabela,
                    Key = chave,
                    UpdateExpression = "SET UltimoErro = :erro ADD Tentativas :um",
                    ExpressionAttributeValues = new()
                    {
                        [":erro"] = new AttributeValue { S = Resumir(ex) },
                        [":um"] = new AttributeValue { N = "1" }
                    }
                }, ct);
                throw;
            }

            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tabela,
                Key = chave,
                UpdateExpression = "SET PublicadaEm = :agora REMOVE GSI1PK, GSI1SK, UltimoErro",
                ExpressionAttributeValues = new() { [":agora"] = new AttributeValue { S = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) } }
            }, ct);
        }

        return pendentes.Count;
    }
}
