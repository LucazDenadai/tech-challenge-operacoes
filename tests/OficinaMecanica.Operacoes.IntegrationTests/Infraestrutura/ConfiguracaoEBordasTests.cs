using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OficinaMecanica.Operacoes.Infrastructure;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Messaging;

namespace OficinaMecanica.Operacoes.IntegrationTests.Infraestrutura;

// Validação de configuração na subida e bordas que não dependem de container.
public class ConfiguracaoEBordasTests
{
    private static IConfiguration Config(params (string Chave, string Valor)[] valores)
        => new ConfigurationBuilder().AddInMemoryCollection(valores.Select(v => new KeyValuePair<string, string?>(v.Chave, v.Valor))).Build();

    [Fact]
    public void Infraestrutura_SemConnectionString_NaoSobe()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddInfrastructure(Config()));

        Assert.Contains("DefaultConnection", ex.Message);
    }

    [Fact]
    public void Infraestrutura_SemSecaoDynamo_UsaOsPadroes()
    {
        var servicos = new ServiceCollection()
            .AddInfrastructure(Config(("ConnectionStrings:DefaultConnection", "Host=localhost;Database=x")))
            .BuildServiceProvider();

        Assert.Equal("operacoes-execucoes", servicos.GetRequiredService<IOptions<DynamoDbOptions>>().Value.Tabela);
    }

    [Fact]
    public void Mensageria_SemSecao_NaoRegistraConsumidor()
    {
        var servicos = new ServiceCollection().AddMensageria(Config());

        Assert.DoesNotContain(servicos, s => s.ImplementationType == typeof(ConsumidorSagaHostedService));
    }

    [Theory]
    [InlineData("", "senha")]
    [InlineData("operacoes", "")]
    public void Mensageria_HabilitadaSemCredencial_NaoSobe(string usuario, string senha)
    {
        var config = Config(("RabbitMq:Enabled", "true"), ("RabbitMq:Username", usuario), ("RabbitMq:Password", senha));

        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMensageria(config));

        Assert.Contains("RabbitMq:Username", ex.Message);
    }

    [Fact]
    public void ClienteDynamo_SemServiceUrl_UsaARegiao()
    {
        using var cliente = new DynamoDbOptions { Regiao = "sa-east-1" }.CriarCliente();

        Assert.Equal("sa-east-1", cliente.Config.RegionEndpoint.SystemName);
    }

    [Theory]
    [InlineData(null, "local")]
    [InlineData("local", null)]
    public void ClienteDynamo_LocalSemCredencialFicticia_NaoSobe(string? accessKey, string? secretKey)
    {
        var opcoes = new DynamoDbOptions { ServiceUrl = "http://localhost:8000", AccessKey = accessKey, SecretKey = secretKey };

        Assert.Throws<InvalidOperationException>(() => opcoes.CriarCliente());
    }

    [Fact]
    public async Task HealthCheckDoConsumidor_SegueOEstado()
    {
        var estado = new EstadoConsumidorSaga();
        var check = new ConsumidorSagaHealthCheck(estado);

        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        estado.Marcar(true);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    // Despachante falso: um lote cheio, uma falha e depois nada pendente.
    private sealed class DespachanteRoteirizado(IOptions<RabbitMqOptions> opcoes) : DespachanteOutboxBase(opcoes, NullLogger.Instance)
    {
        public int Chamadas;
        public readonly TaskCompletionSource Ocioso = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<int> DespacharAsync(CancellationToken ct)
        {
            Chamadas++;
            if (Chamadas == 1)
                return Task.FromResult(Lote);
            if (Chamadas == 2)
                throw new InvalidOperationException("broker fora");
            Ocioso.TrySetResult();
            return Task.FromResult(0);
        }

        public static string ResumirErro(Exception ex) => Resumir(ex);
    }

    [Fact]
    public async Task LacoDoDespachante_LoteCheioSegueFalhaEsperaENadaPendenteDorme()
    {
        var despachante = new DespachanteRoteirizado(Options.Create(new RabbitMqOptions { IntervaloOutboxMs = 50 }));

        await despachante.StartAsync(CancellationToken.None);
        // A falha espera 5 s antes de tentar de novo; a terceira chamada mostra que o laço continuou.
        await despachante.Ocioso.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(200);
        await despachante.StopAsync(CancellationToken.None);

        Assert.True(despachante.Chamadas >= 3);
    }

    [Fact]
    public void ErroLongo_ResumidoEm500Caracteres()
    {
        Assert.Equal(500, DespachanteRoteirizado.ResumirErro(new Exception(new string('x', 800))).Length);
        Assert.Equal("curto", DespachanteRoteirizado.ResumirErro(new Exception("curto")));
    }
}
