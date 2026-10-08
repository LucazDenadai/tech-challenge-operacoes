using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.Application.UseCases.Execucoes;

public class ConsultarExecucoesUseCase(IExecucaoRepository repositorio)
{
    // Fila por filial e estado, na ordem da última atualização (índice GSI1 do DynamoDB).
    public async Task<IReadOnlyList<ExecucaoResponse>> ListarFilaAsync(Guid filialId, StatusExecucao status, CancellationToken ct = default)
        => (await repositorio.ListarFilaAsync(filialId, status, ct)).Select(ExecucaoResponse.De).ToList();

    public async Task<ExecucaoResponse> ObterAsync(Guid id, CancellationToken ct = default)
        => ExecucaoResponse.De(await repositorio.ObterAsync(id, ct) ?? throw new NotFoundException("Execução", id));

    public async Task<ExecucaoResponse> ObterPorOsAsync(Guid osId, CancellationToken ct = default)
        => ExecucaoResponse.De(await repositorio.ObterPorOsAsync(osId, ct) ?? throw new NotFoundException("Execução da OS", osId));
}
