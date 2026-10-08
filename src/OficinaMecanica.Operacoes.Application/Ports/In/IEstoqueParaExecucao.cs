using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.Application.Ports.In;

// Única entrada do módulo Execução (CARD-38b) no Estoque: Execução não lê nem grava
// as tabelas de estoque (decisões do CARD-38). Repetir a chamada não consome de novo.
public interface IEstoqueParaExecucao
{
    Task<bool> FilialOperaEstoqueAsync(Guid filialId, CancellationToken ct = default);
    Task<bool> ReservaAtivaDaOsAsync(Guid reservaId, Guid osId, CancellationToken ct = default);
    Task ConcluirConsumoAsync(Guid reservaId, IReadOnlyList<ItemQuantidade> consumidos, CancellationToken ct = default);
    Task RegistrarConsumoComFalhaAsync(Guid reservaId, IReadOnlyList<ItemQuantidade> consumidos, CancellationToken ct = default);
}
