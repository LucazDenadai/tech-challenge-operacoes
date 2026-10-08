using Amazon.DynamoDBv2;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;

// Cria a tabela antes de a aplicação atender, só com DynamoDb:CriarTabela=true (desenvolvimento e testes).
public class CriarTabelaDynamoHostedService(IAmazonDynamoDB dynamo, IOptions<DynamoDbOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => InicializadorTabelaDynamo.CriarSeNaoExisteAsync(dynamo, options.Value.Tabela, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// Readiness: a tabela de execução existe e responde.
public class DynamoDbHealthCheck(IAmazonDynamoDB dynamo, IOptions<DynamoDbOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var tabela = await dynamo.DescribeTableAsync(options.Value.Tabela, cancellationToken);
            return tabela.Table.TableStatus == "ACTIVE"
                ? HealthCheckResult.Healthy("Tabela de execução ativa.")
                : HealthCheckResult.Unhealthy($"Tabela de execução em {tabela.Table.TableStatus}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("DynamoDB indisponível.", ex);
        }
    }
}
