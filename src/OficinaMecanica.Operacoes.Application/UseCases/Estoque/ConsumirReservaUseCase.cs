using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.In;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.Application.UseCases.Estoque;

// Consumo pedido pela Execução. Grava na própria transação do PostgreSQL: o agregado de
// execução fica no DynamoDB e não há transação entre os dois stores (ADR-016). Por isso a
// chamada é idempotente: a Execução pode repetir após falha ao gravar o próprio estado.
public class ConsumirReservaUseCase(
    IReservaRepository reservaRepository,
    ISaldoEstoqueRepository saldoRepository,
    IMovimentacaoRepository movimentacaoRepository,
    IUnidadeDeTrabalho unidadeDeTrabalho) : IEstoqueParaExecucao
{
    public async Task ConcluirConsumoAsync(Guid reservaId, IReadOnlyList<ItemQuantidade> consumidos, CancellationToken ct = default)
    {
        var reserva = await ObterAsync(reservaId, ct);
        if (reserva.Status == StatusReserva.Consumida)
            return;

        var saldos = await SaldosAsync(reserva, ct);
        await movimentacaoRepository.AdicionarAsync(reserva.Concluir(consumidos, saldos), ct);
        await unidadeDeTrabalho.SalvarAsync(ct);
    }

    public async Task RegistrarConsumoComFalhaAsync(Guid reservaId, IReadOnlyList<ItemQuantidade> consumidos, CancellationToken ct = default)
    {
        var reserva = await ObterAsync(reservaId, ct);
        if (reserva.ConsumoRegistrado)
            return;

        var saldos = await SaldosAsync(reserva, ct);
        await movimentacaoRepository.AdicionarAsync(reserva.RegistrarFalha(consumidos, saldos), ct);
        await unidadeDeTrabalho.SalvarAsync(ct);
    }

    private async Task<Reserva> ObterAsync(Guid reservaId, CancellationToken ct)
        => await reservaRepository.ObterPorIdAsync(reservaId, ct) ?? throw new NotFoundException("Reserva", reservaId);

    private Task<IReadOnlyDictionary<Guid, SaldoEstoque>> SaldosAsync(Reserva reserva, CancellationToken ct)
        => saldoRepository.ObterPorPecasAsync(reserva.FilialId, reserva.Itens.Select(i => i.PecaId), ct);
}
