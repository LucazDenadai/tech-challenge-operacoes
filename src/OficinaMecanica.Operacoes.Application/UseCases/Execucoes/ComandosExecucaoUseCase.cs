using OficinaMecanica.Operacoes.Application.Contratos.Saga;
using OficinaMecanica.Operacoes.Application.Ports.In;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Application.UseCases.Mensageria;
using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.Application.UseCases.Execucoes;

// Comandos da Saga para a Execução. Inbox, agregado e evento são gravados juntos no DynamoDB (ADR-016).
public class ComandosExecucaoUseCase(IExecucaoRepository repositorio, IEstoqueParaExecucao estoque)
{
    public async Task<StatusProcessamento> ProcessarAsync(MensagemRecebida recebida, CancellationToken ct = default)
    {
        var (execucao, eventos) = recebida.Mensagem switch
        {
            DiagnosisRequested pedido => await DiagnosticoAsync(pedido, ct),
            ExecutionStartRequested pedido => await InicioAsync(pedido, ct),
            var outra => throw new InvalidOperationException($"Sem tratamento de execução para {outra.MessageType}.")
        };

        return await repositorio.GravarAsync(execucao, eventos, recebida, ct)
            ? StatusProcessamento.Processada
            : StatusProcessamento.Duplicada;
    }

    private async Task<(Execucao?, IReadOnlyList<MensagemSaga>)> DiagnosticoAsync(DiagnosisRequested pedido, CancellationToken ct)
    {
        // Uma execução por OS: outro pedido para a mesma OS só registra a mensagem recebida.
        if (await repositorio.ObterPorOsAsync(pedido.OsId, ct) is not null)
            return (null, []);

        var execucao = Execucao.Abrir(pedido.OsId, pedido.FilialId, pedido.VeiculoId, pedido.CorrelationId, pedido.MessageId);
        if (await estoque.FilialOperaEstoqueAsync(pedido.FilialId, ct))
            return (execucao, []);

        // Filial onde Operações não opera: rejeita sem esperar o técnico (DiagnosisRejected, ADR-017).
        const string motivo = "A filial não opera em Operações.";
        execucao.RejeitarDiagnostico(motivo, Execucao.Sistema);
        return (execucao, [RespostaSaga.DiagnosticoRejeitado(OrigemEvento.De(pedido), motivo)]);
    }

    private async Task<(Execucao?, IReadOnlyList<MensagemSaga>)> InicioAsync(ExecutionStartRequested pedido, CancellationToken ct)
    {
        var execucao = await repositorio.ObterAsync(pedido.ExecutionId, ct);

        (Execucao?, IReadOnlyList<MensagemSaga>) Recusar(string motivo)
            => (null, [RespostaSaga.InicioRecusado(OrigemEvento.De(pedido), pedido.ExecutionId, motivo)]);

        if (execucao is null || execucao.OsId != pedido.OsId)
            return Recusar("Execução não encontrada para a OS.");

        // Mesmo comando em outra mensagem (mesma idempotencyKey): o início já foi aceito e anunciado.
        if (execucao.ChaveInicio is not null && execucao.ChaveInicio == pedido.IdempotencyKey)
            return (null, []);

        if (execucao.Status != StatusExecucao.Diagnosticada)
            return Recusar($"Execução no status '{execucao.Status}' não pode ser iniciada.");

        if (!await estoque.ReservaAtivaDaOsAsync(pedido.ReservationId, pedido.OsId, ct))
            return Recusar("Reserva inexistente, já encerrada ou de outra OS.");

        execucao.Enfileirar(pedido.ReservationId, pedido.MessageId, pedido.IdempotencyKey!);
        return (execucao, [RespostaSaga.Iniciada(OrigemEvento.De(pedido), execucao.Id, execucao.EnfileiradaEm!.Value)]);
    }
}
