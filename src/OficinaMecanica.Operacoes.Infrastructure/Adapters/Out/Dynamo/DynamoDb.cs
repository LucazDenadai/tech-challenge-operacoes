using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;

namespace OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;

public class DynamoDbOptions
{
    public const string Secao = "DynamoDb";

    // Endpoint do DynamoDB Local (http://localhost:8000 no host, http://dynamodb-local:8000 no Compose).
    // Vazio na nuvem: o SDK usa o endpoint da região e as credenciais do ambiente (IAM).
    public string? ServiceUrl { get; set; }
    public string Regiao { get; set; } = "us-east-1";
    // Credenciais fictícias, só para o DynamoDB Local. Não são credenciais AWS (ADR-016).
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string Tabela { get; set; } = "operacoes-execucoes";
    // Só em desenvolvimento e testes. Na nuvem a tabela vem da IaC (CARD-41).
    public bool CriarTabela { get; set; }

    public IAmazonDynamoDB CriarCliente()
    {
        if (string.IsNullOrWhiteSpace(ServiceUrl))
            return new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(Regiao));

        if (string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey))
            throw new InvalidOperationException("DynamoDb:AccessKey e DynamoDb:SecretKey (fictícias) são obrigatórias com DynamoDb:ServiceUrl.");

        return new AmazonDynamoDBClient(new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonDynamoDBConfig { ServiceURL = ServiceUrl, AuthenticationRegion = Regiao });
    }
}

// Chaves da tabela única de Operações (decisões do CARD-38b).
public static class ChavesDynamo
{
    public const string Pk = "PK";
    public const string Sk = "SK";
    public const string Gsi1 = "GSI1";
    public const string Gsi1Pk = "GSI1PK";
    public const string Gsi1Sk = "GSI1SK";
    public const string OutboxPendente = "OUTBOX#PENDENTE";

    public static string Execucao(Guid id) => $"EXEC#{id}";
    public static string ExecucaoDaOs(Guid osId) => $"OS#{osId}";
    public static string Inbox(Guid messageId) => $"INBOX#{messageId}";
    public static string Outbox(Guid messageId) => $"OUTBOX#{messageId}";
    public static string Fila(Guid filialId, object status) => $"FILIAL#{filialId}#STATUS#{status}";
}

public static class InicializadorTabelaDynamo
{
    // Cria a tabela e o GSI1 quando não existem. Usado só com DynamoDb:CriarTabela=true.
    public static async Task CriarSeNaoExisteAsync(IAmazonDynamoDB cliente, string tabela, CancellationToken ct = default)
    {
        try
        {
            await cliente.DescribeTableAsync(tabela, ct);
            return;
        }
        catch (ResourceNotFoundException)
        {
        }

        await cliente.CreateTableAsync(new CreateTableRequest
        {
            TableName = tabela,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions =
            [
                new AttributeDefinition(ChavesDynamo.Pk, ScalarAttributeType.S),
                new AttributeDefinition(ChavesDynamo.Sk, ScalarAttributeType.S),
                new AttributeDefinition(ChavesDynamo.Gsi1Pk, ScalarAttributeType.S),
                new AttributeDefinition(ChavesDynamo.Gsi1Sk, ScalarAttributeType.S)
            ],
            KeySchema = [new KeySchemaElement(ChavesDynamo.Pk, KeyType.HASH), new KeySchemaElement(ChavesDynamo.Sk, KeyType.RANGE)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = ChavesDynamo.Gsi1,
                    KeySchema = [new KeySchemaElement(ChavesDynamo.Gsi1Pk, KeyType.HASH), new KeySchemaElement(ChavesDynamo.Gsi1Sk, KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL }
                }
            ]
        }, ct);
    }
}
