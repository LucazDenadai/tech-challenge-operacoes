namespace OficinaMecanica.Operacoes.Application.Ports.Out;

public interface IRepository<T> where T : class
{
    Task<T?> ObterPorIdAsync(Guid id, CancellationToken ct = default);
    Task AdicionarAsync(T entidade, CancellationToken ct = default);
}

// Grava em uma única transação tudo o que os repositórios alteraram (agregados, movimentações,
// inbox e outbox). Assim o efeito e a mensagem que o anuncia são confirmados juntos (ADR-016).
public interface IUnidadeDeTrabalho
{
    Task SalvarAsync(CancellationToken ct = default);
}
