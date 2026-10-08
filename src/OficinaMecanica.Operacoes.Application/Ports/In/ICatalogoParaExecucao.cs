using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.Application.Ports.In;

public sealed record ItemSolicitado(TipoItemDiagnostico Tipo, Guid ItemId, int Quantidade);

// Entrada da Execução no Catálogo: devolve os itens com o preço vigente (snapshot do diagnóstico, ADR-015).
public interface ICatalogoParaExecucao
{
    // Item inexistente: NotFoundException. Item inativo: InvalidOperationException.
    Task<IReadOnlyList<ItemDiagnostico>> PrecificarAsync(IReadOnlyList<ItemSolicitado> itens, CancellationToken ct = default);
}
