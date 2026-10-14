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
            if (!await ReservarAsync(chave, ct))
                continue; // outra réplica reservou o item

            try
            {
                await publicador.PublicarAsync(new MensagemParaPublicar(
                    Guid.Parse(item["MessageId"].S), item["MessageType"].S, item["Canal"].S, Guid.Parse(item["CorrelationId"].S),
                    item["Payload"].S, item.TryGetValue("TraceParent", out var trace) ? trace.S : null), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                TelemetriaMensageria.Publicacao(sucesso: false);
                await dynamo.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tabela,
                    Key = chave,
                    // Libera a reserva para a próxima tentativa não esperar o prazo vencer.
                    UpdateExpression = "SET UltimoErro = :erro ADD Tentativas :um REMOVE ReservadoAte",
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
                UpdateExpression = "SET PublicadaEm = :agora REMOVE GSI1PK, GSI1SK, UltimoErro, ReservadoAte",
                ExpressionAttributeValues = new() { [":agora"] = new AttributeValue { S = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) } }
            }, ct);
            TelemetriaMensageria.Publicacao(sucesso: true);
        }

        return pendentes.Count;
    }

    // DynamoDB não tem SKIP LOCKED: cada item é reservado por uma escrita condicional antes de publicar (CARD-41b).
    // Só uma réplica consegue a reserva enquanto ela vale; se a réplica cair depois de reservar, o prazo vence e outra
    // publica (entrega ao menos uma vez, o OS deduplica por messageId).
    private async Task<bool> ReservarAsync(Dictionary<string, AttributeValue> chave, CancellationToken ct)
    {
        var agora = DateTime.UtcNow;
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tabela,
                Key = chave,
                UpdateExpression = "SET ReservadoAte = :ate",
                ConditionExpression = "attribute_exists(GSI1PK) AND (attribute_not_exists(ReservadoAte) OR ReservadoAte < :agora)",
                ExpressionAttributeValues = new()
                {
                    [":ate"] = new AttributeValue { S = (agora + PrazoReserva).ToString("O", CultureInfo.InvariantCulture) },
                    [":agora"] = new AttributeValue { S = agora.ToString("O", CultureInfo.InvariantCulture) }
                }
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private static readonly TimeSpan PrazoReserva = TimeSpan.FromSeconds(60);
}
