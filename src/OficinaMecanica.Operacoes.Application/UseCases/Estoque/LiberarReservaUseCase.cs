using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.Application.UseCases.Estoque;

public sealed record ReservaLiberada(Guid ReservaId, IReadOnlyList<ItemQuantidade> ItensLiberados);

// Efeito do InventoryReleaseRequested (compensação). Idempotente: repetir devolve o mesmo
// resultado. Não grava: quem chama inclui o InventoryReleased na outbox e grava junto.
public class LiberarReservaUseCase(
    IReservaRepository reservaRepository,
    ISaldoEstoqueRepository saldoRepository,
    IMovimentacaoRepository movimentacaoRepository)
{
    public async Task<ReservaLiberada> ExecutarAsync(Guid reservaId, string motivo, CancellationToken ct = default)
    {
        var reserva = await reservaRepository.ObterPorIdAsync(reservaId, ct) ?? throw new NotFoundException("Reserva", reservaId);

        if (reserva.Status == StatusReserva.Ativa)
        {
            var saldos = await saldoRepository.ObterPorPecasAsync(reserva.FilialId, reserva.Itens.Select(i => i.PecaId), ct);
            await movimentacaoRepository.AdicionarAsync(reserva.Liberar(motivo, saldos), ct);
        }

        var liberados = reserva.Itens
            .Where(i => i.QuantidadeLiberada > 0)
            .Select(i => new ItemQuantidade(i.PecaId, i.QuantidadeLiberada))
            .ToList();
        return new ReservaLiberada(reserva.Id, liberados);
    }
}
