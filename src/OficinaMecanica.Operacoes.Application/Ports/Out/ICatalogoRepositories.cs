using OficinaMecanica.Operacoes.Domain.Catalogo;

namespace OficinaMecanica.Operacoes.Application.Ports.Out;

public interface IPecaRepository : IRepository<Peca>
{
    Task<Peca?> ObterPorCodigoAsync(string codigo, CancellationToken ct = default);
    Task<IReadOnlyList<Peca>> ListarAsync(bool incluirInativas, CancellationToken ct = default);
    Task<IReadOnlyList<Peca>> ObterPorIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
}

public interface IServicoRepository : IRepository<Servico>
{
    Task<IReadOnlyList<Servico>> ListarAsync(bool incluirInativos, CancellationToken ct = default);
}
