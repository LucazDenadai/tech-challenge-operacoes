using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using OficinaMecanica.Operacoes.API.Extensions;
using OficinaMecanica.Operacoes.IntegrationTests.Api;
using OficinaMecanica.Operacoes.IntegrationTests.Fixtures;
using OpenTelemetry.Trace;

namespace OficinaMecanica.Operacoes.IntegrationTests.Observabilidade;

// Telemetria do serviço (CARD-42a): o que começa trace e a identidade no /metrics.
[Collection(PostgresCollection.Nome)]
public class TelemetriaTests(PostgresFixture postgres, DynamoDbFixture dynamo)
{
    [Theory]
    [InlineData(ActivityKind.Server, SamplingDecision.RecordAndSample)]
    [InlineData(ActivityKind.Consumer, SamplingDecision.RecordAndSample)]
    [InlineData(ActivityKind.Producer, SamplingDecision.RecordAndSample)]
    [InlineData(ActivityKind.Client, SamplingDecision.Drop)]
    [InlineData(ActivityKind.Internal, SamplingDecision.Drop)]
    public void Amostrador_SoComecaTraceNoInicioDeUmFluxo(ActivityKind tipo, SamplingDecision esperado)
    {
        var parametros = new SamplingParameters(default, ActivityTraceId.CreateRandom(), "span", tipo);

        Assert.Equal(esperado, new AmostradorInicioDeFluxo().ShouldSample(parametros).Decision);
    }

    [Theory]
    [InlineData("/operacoes/health", false)]
    [InlineData("/operacoes/ready", false)]
    [InlineData("/metrics", false)]
    [InlineData("/operacoes/api/execucoes", true)]
    [InlineData("/operacoes/api/execucoes/health-check", true)]
    public void ProbesEScrape_NaoViramTrace(string caminho, bool rastreia)
        => Assert.Equal(rastreia, AmostradorInicioDeFluxo.DeveRastrear(new PathString(caminho)));

    [Fact]
    public async Task Metrics_ExpoeIdentidadeDoServico()
    {
        await using var api = new OperacoesApiFactory(postgres.NovoBancoVazio(), dynamo).WithWebHostBuilder(b => b.UseSetting("Servico:Versao", "sha-teste"));
        var corpo = await api.CreateClient().GetStringAsync("/metrics");

        Assert.Contains("service_name=\"operacoes\"", corpo);
        Assert.Contains("service_namespace=\"oficina-mecanica\"", corpo);
        Assert.Contains("service_version=\"sha-teste\"", corpo);
        Assert.Contains("deployment_environment=\"Test\"", corpo);
    }
}
