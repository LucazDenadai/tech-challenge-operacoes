using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.Application.UseCases.Estoque;

public sealed record ReservarEstoqueCommand(
    Guid OsId, Guid FilialId, Guid CorrelationId, string IdempotencyKey, IReadOnlyList<ItemQuantidade> Itens);

public abstract record ResultadoReserva;
public sealed record ReservaConfirmada(Guid ReservaId, IReadOnlyList<ItemQuantidade> Itens) : ResultadoReserva;
public sealed record ReservaRecusada(string Motivo, IReadOnlyList<Guid> PecasIndisponiveis) : ResultadoReserva;

// Efeito do InventoryReservationRequested. Não grava: quem chama inclui a mensagem de
// resultado na outbox e grava tudo em uma transação (IUnidadeDeTrabalho).
public class ReservarEstoqueUseCase(
    IFilialEstoqueRepository filialRepository,
    ISaldoEstoqueRepository saldoRepository,
    IReservaRepository reservaRepository,
    IMovimentacaoRepository movimentacaoRepository)
{
    public async Task<ResultadoReserva> ExecutarAsync(ReservarEstoqueCommand comando, CancellationToken ct = default)
    {
        // Mesmo comando de novo (outra mensagem, mesma chave): devolve a reserva já feita.
        var existente = await reservaRepository.ObterPorIdempotencyKeyAsync(comando.IdempotencyKey, ct);
        if (existente is not null)
            return Confirmada(existente);

        var filial = await filialRepository.ObterPorIdAsync(comando.FilialId, ct);
        if (filial is null || !filial.Ativo)
            return new ReservaRecusada("A filial não opera estoque em Operações.", []);

        var saldos = await saldoRepository.ObterPorPecasAsync(comando.FilialId, comando.Itens.Select(i => i.PecaId), ct);
        try
        {
            var (reserva, movimentacoes) = Reserva.Reservar(
                comando.OsId, comando.FilialId, comando.CorrelationId, comando.IdempotencyKey, comando.Itens, saldos);

            await reservaRepository.AdicionarAsync(reserva, ct);
            await movimentacaoRepository.AdicionarAsync(movimentacoes, ct);
            return Confirmada(reserva);
        }
        catch (EstoqueInsuficienteException ex)
        {
            return new ReservaRecusada("Saldo insuficiente na filial.", ex.PecasIndisponiveis);
        }
        catch (ArgumentException ex)
        {
            return new ReservaRecusada(ex.Message, []);
        }
    }

    private static ReservaConfirmada Confirmada(Reserva r)
        => new(r.Id, r.Itens.Select(i => new ItemQuantidade(i.PecaId, i.Quantidade)).ToList());
}
