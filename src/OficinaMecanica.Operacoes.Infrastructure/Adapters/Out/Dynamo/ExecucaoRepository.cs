using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using OficinaMecanica.Operacoes.Application.Contratos.Saga;
using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;

// Agregado de execução, inbox e outbox no DynamoDB, gravados juntos por TransactWriteItems (ADR-016).
// O agregado fica como documento JSON (formato evolutivo); chaves, status e versão ficam em atributos próprios.
public class ExecucaoRepository(IAmazonDynamoDB dynamo, IOptions<DynamoDbOptions> options) : IExecucaoRepository
{
    private const string Documento = "Documento";
    private const string Versao = "Versao";
    private const string CondicaoNovo = "attribute_not_exists(PK)";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _tabela = options.Value.Tabela;

    public async Task<Execucao?> ObterAsync(Guid executionId, CancellationToken ct = default)
    {
        var resposta = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = _tabela,
            Key = Chave(ChavesDynamo.Execucao(executionId)),
            ConsistentRead = true
        }, ct);

        return resposta.Item is { Count: > 0 } item ? Ler(item) : null;
    }

    public async Task<Execucao?> ObterPorOsAsync(Guid osId, CancellationToken ct = default)
    {
        var resposta = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = _tabela,
            Key = Chave(ChavesDynamo.ExecucaoDaOs(osId)),
            ConsistentRead = true
        }, ct);

        return resposta.Item is { Count: > 0 } item ? await ObterAsync(Guid.Parse(item["ExecutionId"].S), ct) : null;
    }

    public async Task<IReadOnlyList<Execucao>> ListarFilaAsync(Guid filialId, StatusExecucao status, CancellationToken ct = default)
    {
        var execucoes = new List<Execucao>();
        Dictionary<string, AttributeValue>? inicio = null;
        do
        {
            var resposta = await dynamo.QueryAsync(new QueryRequest
            {
                TableName = _tabela,
                IndexName = ChavesDynamo.Gsi1,
                KeyConditionExpression = "GSI1PK = :fila",
                ExpressionAttributeValues = new() { [":fila"] = new AttributeValue { S = ChavesDynamo.Fila(filialId, status) } },
                ExclusiveStartKey = inicio
            }, ct);

            execucoes.AddRange((resposta.Items ?? []).Select(Ler));
            inicio = resposta.LastEvaluatedKey is { Count: > 0 } chave ? chave : null;
        } while (inicio is not null);

        return execucoes;
    }

    public async Task<bool> GravarAsync(Execucao? execucao, IReadOnlyList<MensagemSaga> eventos, MensagemRecebida? recebida = null, CancellationToken ct = default)
    {
        var itens = new List<TransactWriteItem>();

        // A inbox vai primeiro: a posição dela identifica a duplicata entre os motivos de cancelamento.
        if (recebida is not null)
            itens.Add(Put(new()
            {
                [ChavesDynamo.Pk] = S(ChavesDynamo.Inbox(recebida.Mensagem.MessageId)),
                [ChavesDynamo.Sk] = S("INBOX"),
                ["MessageType"] = S(recebida.Mensagem.MessageType),
                ["Canal"] = S(recebida.Canal),
                ["CorrelationId"] = S(recebida.Mensagem.CorrelationId.ToString()),
                ["RecebidaEm"] = S(Agora()),
                ["Payload"] = S(recebida.Payload)
            }, CondicaoNovo));

        var novaVersao = 0;
        if (execucao is not null)
        {
            var versaoLida = execucao.Versao;
            novaVersao = versaoLida + 1;
            var estado = execucao.Estado() with { Versao = novaVersao };

            var item = new Dictionary<string, AttributeValue>
            {
                [ChavesDynamo.Pk] = S(ChavesDynamo.Execucao(execucao.Id)),
                [ChavesDynamo.Sk] = S("EXEC"),
                [ChavesDynamo.Gsi1Pk] = S(ChavesDynamo.Fila(execucao.FilialId, execucao.Status)),
                [ChavesDynamo.Gsi1Sk] = S($"{execucao.AtualizadaEm.ToString("O", CultureInfo.InvariantCulture)}#{execucao.Id}"),
                ["OsId"] = S(execucao.OsId.ToString()),
                ["FilialId"] = S(execucao.FilialId.ToString()),
                ["Status"] = S(execucao.Status.ToString()),
                [Versao] = new AttributeValue { N = novaVersao.ToString(CultureInfo.InvariantCulture) },
                [Documento] = S(JsonSerializer.Serialize(estado, _json))
            };

            if (versaoLida == 0)
            {
                itens.Add(Put(item, CondicaoNovo));
                // Uma execução por OS: o segundo registro para a mesma OS falha a condição.
                itens.Add(Put(new()
                {
                    [ChavesDynamo.Pk] = S(ChavesDynamo.ExecucaoDaOs(execucao.OsId)),
                    [ChavesDynamo.Sk] = S("EXEC"),
                    ["ExecutionId"] = S(execucao.Id.ToString())
                }, CondicaoNovo));
            }
            else
            {
                itens.Add(new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = _tabela,
                        Item = item,
                        ConditionExpression = "Versao = :versaoLida",
                        ExpressionAttributeValues = new() { [":versaoLida"] = new AttributeValue { N = versaoLida.ToString(CultureInfo.InvariantCulture) } }
                    }
                });
            }
        }

        var traceParent = Activity.Current is { IdFormat: ActivityIdFormat.W3C } atividade ? atividade.Id : null;
        foreach (var evento in eventos)
        {
            var criadaEm = Agora();
            var outbox = new Dictionary<string, AttributeValue>
            {
                [ChavesDynamo.Pk] = S(ChavesDynamo.Outbox(evento.MessageId)),
                [ChavesDynamo.Sk] = S("OUTBOX"),
                // Pendente: entra no GSI1. O despachante remove estes atributos ao publicar.
                [ChavesDynamo.Gsi1Pk] = S(ChavesDynamo.OutboxPendente),
                [ChavesDynamo.Gsi1Sk] = S($"{criadaEm}#{evento.MessageId}"),
                ["MessageId"] = S(evento.MessageId.ToString()),
                ["MessageType"] = S(evento.MessageType),
                ["Canal"] = S(CatalogoCanaisOperacoes.Publicado(evento.GetType()).Endereco),
                ["CorrelationId"] = S(evento.CorrelationId.ToString()),
                ["Payload"] = S(RespostaSaga.Serializar(evento)),
                ["CriadaEm"] = S(criadaEm)
            };
            if (traceParent is not null)
                outbox["TraceParent"] = S(traceParent);
            itens.Add(Put(outbox, CondicaoNovo));
        }

        if (itens.Count == 0)
            return true;

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = itens }, ct);
        }
        catch (TransactionCanceledException ex)
        {
            var motivos = ex.CancellationReasons ?? [];
            if (recebida is not null && motivos.Count > 0 && motivos[0].Code == "ConditionalCheckFailed")
                return false;
            if (motivos.Any(m => m.Code == "ConditionalCheckFailed"))
                throw new ConflitoConcorrenciaException(ex);
            throw;
        }

        execucao?.MarcarGravada(novaVersao);
        return true;
    }

    private TransactWriteItem Put(Dictionary<string, AttributeValue> item, string condicao)
        => new() { Put = new Put { TableName = _tabela, Item = item, ConditionExpression = condicao } };

    private static Execucao Ler(Dictionary<string, AttributeValue> item)
        => Execucao.Reidratar(JsonSerializer.Deserialize<EstadoExecucao>(item[Documento].S, _json)!);

    private static Dictionary<string, AttributeValue> Chave(string pk) => new() { [ChavesDynamo.Pk] = S(pk), [ChavesDynamo.Sk] = S("EXEC") };

    private static AttributeValue S(string valor) => new() { S = valor };

    private static string Agora() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
