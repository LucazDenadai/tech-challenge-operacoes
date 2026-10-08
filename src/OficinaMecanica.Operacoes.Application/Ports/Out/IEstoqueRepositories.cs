using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.Application.Ports.Out;

public interface IFilialEstoqueRepository : IRepository<FilialEstoque>
{
    Task<IReadOnlyList<FilialEstoque>> ListarAsync(CancellationToken ct = default);
}

public interface ISaldoEstoqueRepository
{
    Task<SaldoEstoque?> ObterAsync(Guid filialId, Guid pecaId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, SaldoEstoque>> ObterPorPecasAsync(Guid filialId, IEnumerable<Guid> pecaIds, CancellationToken ct = default);
    Task<IReadOnlyList<SaldoEstoque>> ListarPorFilialAsync(Guid filialId, CancellationToken ct = default);
    Task AdicionarAsync(SaldoEstoque saldo, CancellationToken ct = default);
}

public interface IReservaRepository : IRepository<Reserva>
{
    Task<Reserva?> ObterPorIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
}

public sealed record FiltroMovimentacao(Guid FilialId, Guid? PecaId = null, Guid? OsId = null, Guid? CorrelationId = null, int Limite = 100);

public interface IMovimentacaoRepository
{
    Task AdicionarAsync(IEnumerable<MovimentacaoEstoque> movimentacoes, CancellationToken ct = default);
    Task<IReadOnlyList<MovimentacaoEstoque>> ListarAsync(FiltroMovimentacao filtro, CancellationToken ct = default);
}
