using System.Diagnostics;
using OficinaMecanica.Operacoes.Application.UseCases.Execucoes;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.In.Messaging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OficinaMecanica.Operacoes.API.Extensions;

public static class OpenTelemetryExtensions
{
    private static readonly double[] _baldesOficina = [60, 300, 900, 1800, 3600, 7200, 14400, 28800, 86400];

    public static IHostApplicationBuilder AddOpenTelemetry(
        this IHostApplicationBuilder builder, string serviceName)
    {
        var jaegerEndpoint = builder.Configuration["Jaeger:Endpoint"] ?? "http://jaeger:4318";
        // Versão da imagem (sha-<commit>), gravada no build; "local" fora do CI.
        var versao = builder.Configuration["Servico:Versao"] ?? "local";

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(recurso => recurso
                .AddService(serviceName, serviceNamespace: "oficina-mecanica", serviceVersion: versao)
                .AddAttributes([new("deployment.environment", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new AmostradorInicioDeFluxo()))
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.RecordException = true;
                    o.Filter = contexto => AmostradorInicioDeFluxo.DeveRastrear(contexto.Request.Path);
                })
                .AddEntityFrameworkCoreInstrumentation()
                .AddSource(TelemetriaMensageria.NomeFonte)
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri($"{jaegerEndpoint.TrimEnd('/')}/v1/traces");
                    o.Protocol = OtlpExportProtocol.HttpProtobuf;
                }))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(TelemetriaMensageria.NomeFonte)
                .AddMeter(MetricasExecucao.NomeMedidor)
                // Fila e reparo levam de minutos a um dia.
                .AddView(MetricasExecucao.NomeTempoNaFila, new ExplicitBucketHistogramConfiguration { Boundaries = _baldesOficina })
                .AddView(MetricasExecucao.NomeDuracaoReparo, new ExplicitBucketHistogramConfiguration { Boundaries = _baldesOficina })
                .AddPrometheusExporter());

        return builder;
    }
}

// Só começa um trace o que começa um fluxo: requisição HTTP recebida, mensagem consumida ou publicada (CARD-42a).
// Um SQL sem pai (laço da outbox) não vira trace próprio; dentro de um fluxo, continua como
// span filho, porque o ParentBasedSampler segue a decisão do pai.
public sealed class AmostradorInicioDeFluxo : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
        => new(samplingParameters.Kind is ActivityKind.Server or ActivityKind.Consumer or ActivityKind.Producer
            ? SamplingDecision.RecordAndSample
            : SamplingDecision.Drop);

    // Probes do Kubernetes e scrape do Prometheus não são fluxo de negócio.
    public static bool DeveRastrear(PathString caminho)
        => !(caminho.Value?.EndsWith("/health", StringComparison.OrdinalIgnoreCase) == true
             || caminho.Value?.EndsWith("/ready", StringComparison.OrdinalIgnoreCase) == true
             || caminho.StartsWithSegments("/metrics"));
}
