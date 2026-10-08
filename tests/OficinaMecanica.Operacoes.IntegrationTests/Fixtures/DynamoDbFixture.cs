using Amazon.DynamoDBv2;
using Microsoft.Extensions.Options;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;
using Testcontainers.DynamoDb;

namespace OficinaMecanica.Operacoes.IntegrationTests.Fixtures;

// DynamoDB Local em container, com credenciais fictícias: nenhum teste chama a AWS (ADR-016).
public class DynamoDbFixture : IAsyncLifetime
{
    public const string AccessKey = "local";
    public const string SecretKey = "local";

    private readonly DynamoDbContainer _dynamo = new DynamoDbBuilder("amazon/dynamodb-local:2.6.1").Build();

    public string Endpoint => _dynamo.GetConnectionString();

    public Task InitializeAsync() => _dynamo.StartAsync();

    public Task DisposeAsync() => _dynamo.DisposeAsync().AsTask();

    // Cada teste usa uma tabela própria, criada pelo mesmo inicializador da aplicação.
    public DynamoDbOptions Opcoes(string? tabela = null) => new()
    {
        ServiceUrl = Endpoint,
        AccessKey = AccessKey,
        SecretKey = SecretKey,
        Tabela = tabela ?? $"execucoes-{Guid.NewGuid():N}",
        CriarTabela = true
    };

    public async Task<(IAmazonDynamoDB Cliente, IOptions<DynamoDbOptions> Opcoes)> NovaTabelaAsync()
    {
        var opcoes = Opcoes();
        var cliente = opcoes.CriarCliente();
        await InicializadorTabelaDynamo.CriarSeNaoExisteAsync(cliente, opcoes.Tabela);
        return (cliente, Options.Create(opcoes));
    }
}

[CollectionDefinition(Nome)]
public class DynamoDbCollection : ICollectionFixture<DynamoDbFixture>
{
    public const string Nome = "DynamoDb";
}
