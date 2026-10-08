using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Catalogo;
using OficinaMecanica.Operacoes.Domain.Comum;

namespace OficinaMecanica.Operacoes.Application.UseCases.Catalogo;

public class GerenciarServicoUseCase(IServicoRepository servicoRepository, IUnidadeDeTrabalho unidadeDeTrabalho)
{
    public async Task<IReadOnlyList<ServicoResponse>> ListarAsync(bool incluirInativos, CancellationToken ct = default)
        => (await servicoRepository.ListarAsync(incluirInativos, ct)).Select(ToResponse).ToList();

    public async Task<ServicoResponse> ObterAsync(Guid id, CancellationToken ct = default)
        => ToResponse(await ObterEntidadeAsync(id, ct));

    public async Task<ServicoResponse> CriarAsync(ServicoRequest request, CancellationToken ct = default)
    {
        var servico = new Servico(request.Nome, request.Descricao, request.Preco, request.TempoConclusaoMinutos);
        await servicoRepository.AdicionarAsync(servico, ct);
        await unidadeDeTrabalho.SalvarAsync(ct);
        return ToResponse(servico);
    }

    public async Task<ServicoResponse> AtualizarAsync(Guid id, ServicoRequest request, CancellationToken ct = default)
    {
        var servico = await ObterEntidadeAsync(id, ct);
        servico.Atualizar(request.Nome, request.Descricao, request.Preco, request.TempoConclusaoMinutos);
        await unidadeDeTrabalho.SalvarAsync(ct);
        return ToResponse(servico);
    }

    public async Task DesativarAsync(Guid id, CancellationToken ct = default)
    {
        var servico = await ObterEntidadeAsync(id, ct);
        servico.Desativar();
        await unidadeDeTrabalho.SalvarAsync(ct);
    }

    private async Task<Servico> ObterEntidadeAsync(Guid id, CancellationToken ct)
        => await servicoRepository.ObterPorIdAsync(id, ct) ?? throw new NotFoundException("Serviço", id);

    private static ServicoResponse ToResponse(Servico s)
        => new(s.Id, s.Nome, s.Descricao, s.Preco, Dinheiro.Moeda, s.TempoConclusaoMinutos, s.Ativo);
}
