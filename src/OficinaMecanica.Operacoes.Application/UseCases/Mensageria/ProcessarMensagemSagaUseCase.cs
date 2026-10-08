using System.Text;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Operacoes.Application.Contratos.Saga;
using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Application.UseCases.Estoque;
using OficinaMecanica.Operacoes.Application.UseCases.Execucoes;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.Application.UseCases.Mensageria;

public enum StatusProcessamento
{
    Processada,
    Duplicada,
    Rejeitada
}

public sealed record ResultadoProcessamento(StatusProcessamento Status, string? Motivo, Guid? MessageId, Guid? CorrelationId, string? MessageType);

// Participante da Saga (decisões do CARD-38): valida o comando contra o contrato do canal e, em uma
// única transação, registra a inbox, aplica o efeito e grava o evento de resultado na outbox.
// O store depende do módulo: Estoque no PostgreSQL, Execução no DynamoDB (ADR-016).
// Conflito de concorrência e falha de infraestrutura sobem como exceção: o consumidor refaz a mensagem.
public class ProcessarMensagemSagaUseCase(
    ITransacao transacao,
    IInboxRepository inbox,
    IOutbox outbox,
    IUnidadeDeTrabalho unidadeDeTrabalho,
    ReservarEstoqueUseCase reservar,
    LiberarReservaUseCase liberar,
    ComandosExecucaoUseCase comandosExecucao,
    ILogger<ProcessarMensagemSagaUseCase> logger)
{
    public async Task<ResultadoProcessamento> ExecutarAsync(string canal, ReadOnlyMemory<byte> corpo, CancellationToken ct = default)
    {
        var contrato = CatalogoCanaisOperacoes.ObterConsumido(canal);
        if (contrato is null)
            return new ResultadoProcessamento(StatusProcessamento.Rejeitada, $"Canal '{canal}' não é consumido por Operações.", null, null, null);

        var leitura = LeitorMensagemSaga.Ler(contrato, corpo);
        if (!leitura.Valida)
            return new ResultadoProcessamento(StatusProcessamento.Rejeitada, leitura.Motivo, leitura.MessageId, leitura.CorrelationId, leitura.MessageType);

        var mensagem = leitura.Mensagem!;
        try
        {
            var payload = Encoding.UTF8.GetString(corpo.Span);
            var status = mensagem is DiagnosisRequested or ExecutionStartRequested
                ? await comandosExecucao.ProcessarAsync(new MensagemRecebida(mensagem, canal, payload), ct)
                : await ProcessarEstoqueAsync(mensagem, canal, payload, ct);

            logger.LogInformation("Mensagem {Status}. MessageType={MessageType} OsId={OsId}", status, mensagem.MessageType, mensagem.OsId);
            return new ResultadoProcessamento(status, null, mensagem.MessageId, mensagem.CorrelationId, mensagem.MessageType);
        }
        catch (NotFoundException ex)
        {
            // Referência inexistente não se resolve com nova tentativa: vai para a DLQ com o motivo.
            return new ResultadoProcessamento(StatusProcessamento.Rejeitada, ex.Message, mensagem.MessageId, mensagem.CorrelationId, mensagem.MessageType);
        }
    }

    private Task<StatusProcessamento> ProcessarEstoqueAsync(MensagemSaga mensagem, string canal, string payload, CancellationToken ct)
        => transacao.ExecutarAsync(async () =>
        {
            if (!await inbox.RegistrarAsync(mensagem, canal, payload, ct))
                return StatusProcessamento.Duplicada;

            outbox.Adicionar(await AplicarAsync(mensagem, ct));
            await unidadeDeTrabalho.SalvarAsync(ct);
            return StatusProcessamento.Processada;
        }, ct);

    private async Task<MensagemSaga> AplicarAsync(MensagemSaga mensagem, CancellationToken ct)
    {
        switch (mensagem)
        {
            case InventoryReservationRequested pedido:
            {
                var comando = new ReservarEstoqueCommand(pedido.OsId, pedido.FilialId, pedido.CorrelationId, pedido.IdempotencyKey!,
                    pedido.Items.Select(i => new ItemQuantidade(i.PecaId, i.Quantity)).ToList());

                return await reservar.ExecutarAsync(comando, ct) switch
                {
                    ReservaConfirmada ok => RespostaSaga.Reservado(pedido, ok.ReservaId, Pecas(ok.Itens)),
                    ReservaRecusada recusa => RespostaSaga.ReservaRecusada(pedido, recusa.Motivo, recusa.PecasIndisponiveis),
                    var outro => throw new InvalidOperationException($"Resultado de reserva inesperado: {outro}.")
                };
            }

            case InventoryReleaseRequested pedido:
            {
                var liberada = await liberar.ExecutarAsync(pedido.ReservationId, pedido.Reason, ct);
                return RespostaSaga.Liberado(pedido, liberada.ReservaId, Pecas(liberada.ItensLiberados));
            }

            default:
                throw new InvalidOperationException($"Sem tratamento para {mensagem.MessageType}.");
        }
    }

    private static IReadOnlyList<PecaQuantity> Pecas(IEnumerable<ItemQuantidade> itens)
        => itens.Select(i => new PecaQuantity { PecaId = i.PecaId, Quantity = i.Quantidade }).ToList();
}
