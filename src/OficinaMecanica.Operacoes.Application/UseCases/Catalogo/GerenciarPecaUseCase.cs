using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Catalogo;
using OficinaMecanica.Operacoes.Domain.Comum;

namespace OficinaMecanica.Operacoes.Application.UseCases.Catalogo;

public class GerenciarPecaUseCase(IPecaRepository pecaRepository, IUnidadeDeTrabalho unidadeDeTrabalho)
{
    public async Task<IReadOnlyList<PecaResponse>> ListarAsync(bool incluirInativas, CancellationToken ct = default)
        => (await pecaRepository.ListarAsync(incluirInativas, ct)).Select(ToResponse).ToList();

    public async Task<PecaResponse> ObterAsync(Guid id, CancellationToken ct = default)
        => ToResponse(await ObterEntidadeAsync(id, ct));

    public async Task<PecaResponse> CriarAsync(CriarPecaRequest request, CancellationToken ct = default)
    {
        var peca = new Peca(request.Codigo, request.Nome, request.Descricao, request.PrecoTabela);
        if (await pecaRepository.ObterPorCodigoAsync(peca.Codigo, ct) is not null)
            throw new InvalidOperationException($"Já existe uma peça com o código '{peca.Codigo}'.");

        await pecaRepository.AdicionarAsync(peca, ct);
        await unidadeDeTrabalho.SalvarAsync(ct);
        return ToResponse(peca);
    }

    public async Task<PecaResponse> AtualizarAsync(Guid id, AtualizarPecaRequest request, CancellationToken ct = default)
    {
        var peca = await ObterEntidadeAsync(id, ct);
        peca.Atualizar(request.Nome, request.Descricao, request.PrecoTabela);
        await unidadeDeTrabalho.SalvarAsync(ct);
        return ToResponse(peca);
    }

    public async Task DesativarAsync(Guid id, CancellationToken ct = default)
    {
        var peca = await ObterEntidadeAsync(id, ct);
        peca.Desativar();
        await unidadeDeTrabalho.SalvarAsync(ct);
    }

    private async Task<Peca> ObterEntidadeAsync(Guid id, CancellationToken ct)
        => await pecaRepository.ObterPorIdAsync(id, ct) ?? throw new NotFoundException("Peça", id);

    internal static PecaResponse ToResponse(Peca p)
        => new(p.Id, p.Codigo, p.Nome, p.Descricao, p.PrecoTabela, Dinheiro.Moeda, p.Ativo);
}
