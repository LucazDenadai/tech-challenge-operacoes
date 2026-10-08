using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Mensageria;
using RabbitMQ.Client;

namespace OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Messaging;

// Publica os eventos gravados na outbox (ADR-016) na exchange fanout do canal (ADR-018) e marca a publicação.
// Entrega pelo menos uma vez: se cair entre publicar e marcar, publica de novo; o consumidor deduplica por messageId.
public class DespachanteOutboxHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<DespachanteOutboxHostedService> logger) : BackgroundService
{
    private const int Lote = 50;

    private readonly RabbitMqOptions _opcoes = options.Value;
    private readonly HashSet<string> _exchangesDeclaradas = [];
    private IConnection? _conexao;
    private IChannel? _canal;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = TimeSpan.FromMilliseconds(_opcoes.IntervaloOutboxMs);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await DespacharAsync(stoppingToken) == Lote)
                    continue; // há mais pendentes: segue sem esperar
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Falha ao despachar a outbox; nova tentativa em {Espera}s.", 5);
                await FecharAsync();
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            await Task.Delay(intervalo, stoppingToken);
        }
    }

    // Retorna quantas mensagens pendentes foram lidas.
    public async Task<int> DespacharAsync(CancellationToken ct)
    {
        using var escopo = scopeFactory.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        var pendentes = await db.Outbox
            .Where(m => m.PublicadaEmUtc == null)
            .OrderBy(m => m.CriadaEmUtc)
            .Take(Lote)
            .ToListAsync(ct);
        if (pendentes.Count == 0)
            return 0;

        var canal = await ObterCanalAsync(ct);
        foreach (var mensagem in pendentes)
        {
            try
            {
                await PublicarAsync(canal, mensagem, ct);
                mensagem.PublicadaEmUtc = DateTimeOffset.UtcNow;
                mensagem.UltimoErro = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                mensagem.Tentativas++;
                mensagem.UltimoErro = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                await db.SaveChangesAsync(ct);
                throw;
            }

            await db.SaveChangesAsync(ct);
        }

        return pendentes.Count;
    }

    private async Task PublicarAsync(IChannel canal, MensagemOutbox mensagem, CancellationToken ct)
    {
        if (_exchangesDeclaradas.Add(mensagem.Canal))
            await canal.ExchangeDeclareAsync(mensagem.Canal, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);

        // Span de publicação filho do trace que gerou o evento; o traceparent vai no cabeçalho para o consumidor continuar.
        ActivityContext.TryParse(mensagem.TraceParent, null, isRemote: false, out var pai);
        using var atividade = TelemetriaMensageria.Fonte.StartActivity($"{mensagem.Canal} publish", ActivityKind.Producer, pai);
        atividade?.SetTag("messaging.system", "rabbitmq");
        atividade?.SetTag("messaging.destination.name", mensagem.Canal);
        atividade?.SetTag("messaging.operation.type", "publish");
        atividade?.SetTag("messaging.message.id", mensagem.MessageId.ToString());
        atividade?.SetTag("correlation_id", mensagem.CorrelationId.ToString());

        var cabecalhos = new Dictionary<string, object?>();
        var traceParent = atividade?.Id ?? mensagem.TraceParent;
        if (traceParent is not null)
            cabecalhos["traceparent"] = traceParent;

        var propriedades = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = mensagem.MessageId.ToString(),
            CorrelationId = mensagem.CorrelationId.ToString(),
            Type = mensagem.MessageType,
            Headers = cabecalhos
        };

        // Com confirmação do broker, o await só termina depois que o RabbitMQ aceitou a mensagem.
        await canal.BasicPublishAsync(mensagem.Canal, string.Empty, mandatory: false, propriedades,
            Encoding.UTF8.GetBytes(mensagem.Payload), ct);
    }

    private async Task<IChannel> ObterCanalAsync(CancellationToken ct)
    {
        if (_canal is { IsOpen: true })
            return _canal;

        await FecharAsync();
        _conexao = await _opcoes.CriarFabrica("tech-challenge-operacoes-outbox").CreateConnectionAsync(ct);
        _canal = await _conexao.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        return _canal;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FecharAsync();
    }

    private async Task FecharAsync()
    {
        _exchangesDeclaradas.Clear();
        if (_canal is not null) { await _canal.DisposeAsync(); _canal = null; }
        if (_conexao is not null) { await _conexao.DisposeAsync(); _conexao = null; }
    }
}
