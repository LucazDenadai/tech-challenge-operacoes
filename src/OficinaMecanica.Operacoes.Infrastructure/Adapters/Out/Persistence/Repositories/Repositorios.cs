using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Catalogo;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Repositories;

public class UnidadeDeTrabalho(AppDbContext context) : IUnidadeDeTrabalho
{
    public async Task SalvarAsync(CancellationToken ct = default)
    {
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConflitoConcorrenciaException(ex);
        }
    }
}

public class PecaRepository(AppDbContext context) : IPecaRepository
{
    public async Task<Peca?> ObterPorIdAsync(Guid id, CancellationToken ct = default) => await context.Pecas.FindAsync([id], ct);

    public async Task AdicionarAsync(Peca entidade, CancellationToken ct = default) => await context.Pecas.AddAsync(entidade, ct);

    public Task<Peca?> ObterPorCodigoAsync(string codigo, CancellationToken ct = default)
        => context.Pecas.FirstOrDefaultAsync(p => p.Codigo == codigo, ct);

    public async Task<IReadOnlyList<Peca>> ListarAsync(bool incluirInativas, CancellationToken ct = default)
        => await context.Pecas.Where(p => incluirInativas || p.Ativo).OrderBy(p => p.Codigo).ToListAsync(ct);

    public async Task<IReadOnlyList<Peca>> ObterPorIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var lista = ids.Distinct().ToList();
        return await context.Pecas.Where(p => lista.Contains(p.Id)).ToListAsync(ct);
    }
}

public class ServicoRepository(AppDbContext context) : IServicoRepository
{
    public async Task<Servico?> ObterPorIdAsync(Guid id, CancellationToken ct = default) => await context.Servicos.FindAsync([id], ct);

    public async Task AdicionarAsync(Servico entidade, CancellationToken ct = default) => await context.Servicos.AddAsync(entidade, ct);

    public async Task<IReadOnlyList<Servico>> ListarAsync(bool incluirInativos, CancellationToken ct = default)
        => await context.Servicos.Where(s => incluirInativos || s.Ativo).OrderBy(s => s.Nome).ToListAsync(ct);
}

public class FilialEstoqueRepository(AppDbContext context) : IFilialEstoqueRepository
{
    public async Task<FilialEstoque?> ObterPorIdAsync(Guid id, CancellationToken ct = default) => await context.Filiais.FindAsync([id], ct);

    public async Task AdicionarAsync(FilialEstoque entidade, CancellationToken ct = default) => await context.Filiais.AddAsync(entidade, ct);

    public async Task<IReadOnlyList<FilialEstoque>> ListarAsync(CancellationToken ct = default)
        => await context.Filiais.OrderBy(f => f.Codigo).ToListAsync(ct);
}

public class SaldoEstoqueRepository(AppDbContext context) : ISaldoEstoqueRepository
{
    public async Task<SaldoEstoque?> ObterAsync(Guid filialId, Guid pecaId, CancellationToken ct = default)
        => await context.Saldos.FindAsync([filialId, pecaId], ct);

    public async Task<IReadOnlyDictionary<Guid, SaldoEstoque>> ObterPorPecasAsync(Guid filialId, IEnumerable<Guid> pecaIds, CancellationToken ct = default)
    {
        var lista = pecaIds.Distinct().ToList();
        return await context.Saldos
            .Where(s => s.FilialId == filialId && lista.Contains(s.PecaId))
            .ToDictionaryAsync(s => s.PecaId, ct);
    }

    public async Task<IReadOnlyList<SaldoEstoque>> ListarPorFilialAsync(Guid filialId, CancellationToken ct = default)
        => await context.Saldos.Where(s => s.FilialId == filialId).ToListAsync(ct);

    public async Task AdicionarAsync(SaldoEstoque saldo, CancellationToken ct = default) => await context.Saldos.AddAsync(saldo, ct);
}

public class ReservaRepository(AppDbContext context) : IReservaRepository
{
    public Task<Reserva?> ObterPorIdAsync(Guid id, CancellationToken ct = default)
        => context.Reservas.Include(r => r.Itens).FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task AdicionarAsync(Reserva entidade, CancellationToken ct = default) => await context.Reservas.AddAsync(entidade, ct);

    public Task<Reserva?> ObterPorIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
        => context.Reservas.Include(r => r.Itens).FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, ct);
}

public class MovimentacaoRepository(AppDbContext context) : IMovimentacaoRepository
{
    public Task AdicionarAsync(IEnumerable<MovimentacaoEstoque> movimentacoes, CancellationToken ct = default)
        => context.Movimentacoes.AddRangeAsync(movimentacoes, ct);

    public async Task<IReadOnlyList<MovimentacaoEstoque>> ListarAsync(FiltroMovimentacao filtro, CancellationToken ct = default)
    {
        var consulta = context.Movimentacoes.AsNoTracking().Where(m => m.FilialId == filtro.FilialId);
        if (filtro.PecaId is { } pecaId)
            consulta = consulta.Where(m => m.PecaId == pecaId);
        if (filtro.OsId is { } osId)
            consulta = consulta.Where(m => m.OsId == osId);
        if (filtro.CorrelationId is { } correlationId)
            consulta = consulta.Where(m => m.CorrelationId == correlationId);

        return await consulta.OrderByDescending(m => m.OcorridoEm).Take(filtro.Limite).ToListAsync(ct);
    }
}
