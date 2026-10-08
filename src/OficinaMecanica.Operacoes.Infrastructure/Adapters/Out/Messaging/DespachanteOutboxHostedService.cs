using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence;

namespace OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Messaging;

// Laço comum dos despachantes: lê um lote de pendentes, publica e espera o intervalo quando não há mais.
// Entrega pelo menos uma vez: se cair entre publicar e marcar, publica de novo; o consumidor deduplica por messageId.
public abstract class DespachanteOutboxBase(IOptions<RabbitMqOptions> options, ILogger logger) : BackgroundService
{
    protected const int Lote = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = TimeSpan.FromMilliseconds(options.Value.IntervaloOutboxMs);
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
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            await Task.Delay(intervalo, stoppingToken);
        }
    }

    // Retorna quantas mensagens pendentes foram lidas.
    public abstract Task<int> DespacharAsync(CancellationToken ct);

    protected static string Resumir(Exception ex) => ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
}

// Outbox do PostgreSQL: eventos do Estoque (ADR-016).
public class DespachanteOutboxHostedService(
    IServiceScopeFactory scopeFactory,
    PublicadorRabbitMq publicador,
    IOptions<RabbitMqOptions> options,
    ILogger<DespachanteOutboxHostedService> logger) : DespachanteOutboxBase(options, logger)
{
    public override async Task<int> DespacharAsync(CancellationToken ct)
    {
        using var escopo = scopeFactory.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        var pendentes = await db.Outbox
            .Where(m => m.PublicadaEmUtc == null)
            .OrderBy(m => m.CriadaEmUtc)
            .Take(Lote)
            .ToListAsync(ct);

        foreach (var mensagem in pendentes)
        {
            try
            {
                await publicador.PublicarAsync(new MensagemParaPublicar(mensagem.MessageId, mensagem.MessageType, mensagem.Canal,
                    mensagem.CorrelationId, mensagem.Payload, mensagem.TraceParent), ct);
                mensagem.PublicadaEmUtc = DateTimeOffset.UtcNow;
                mensagem.UltimoErro = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                mensagem.Tentativas++;
                mensagem.UltimoErro = Resumir(ex);
                await db.SaveChangesAsync(ct);
                throw;
            }

            await db.SaveChangesAsync(ct);
        }

        return pendentes.Count;
    }
}
