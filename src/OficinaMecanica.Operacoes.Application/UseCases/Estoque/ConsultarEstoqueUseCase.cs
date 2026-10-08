using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.Application.UseCases.Estoque;

public class ConsultarEstoqueUseCase(
    IFilialEstoqueRepository filialRepository,
    ISaldoEstoqueRepository saldoRepository,
    IPecaRepository pecaRepository,
    IMovimentacaoRepository movimentacaoRepository)
{
    public async Task<IReadOnlyList<FilialEstoqueResponse>> ListarFiliaisAsync(CancellationToken ct = default)
        => (await filialRepository.ListarAsync(ct)).Select(f => new FilialEstoqueResponse(f.Id, f.Codigo, f.Ativo)).ToList();

    public async Task<IReadOnlyList<SaldoResponse>> ListarSaldosAsync(Guid filialId, CancellationToken ct = default)
    {
        await ExigirFilialAsync(filialId, ct);

        var saldos = await saldoRepository.ListarPorFilialAsync(filialId, ct);
        var pecas = (await pecaRepository.ObterPorIdsAsync(saldos.Select(s => s.PecaId), ct)).ToDictionary(p => p.Id);

        return saldos
            .Where(s => pecas.ContainsKey(s.PecaId))
            .Select(s => new SaldoResponse(s.FilialId, s.PecaId, pecas[s.PecaId].Codigo, pecas[s.PecaId].Nome,
                s.QuantidadeDisponivel, s.QuantidadeReservada))
            .OrderBy(s => s.CodigoPeca)
            .ToList();
    }

    // Só leitura: não reserva. A reserva vem pelo comando da Saga (ADR-017).
    public async Task<DisponibilidadeResponse> ConsultarDisponibilidadeAsync(DisponibilidadeRequest request, CancellationToken ct = default)
    {
        await ExigirFilialAsync(request.FilialId, ct);

        var itens = ItemQuantidade.Consolidar(request.Itens.Select(i => new ItemQuantidade(i.PecaId, i.Quantidade)));
        var saldos = await saldoRepository.ObterPorPecasAsync(request.FilialId, itens.Select(i => i.PecaId), ct);

        var resposta = itens.Select(i =>
        {
            var disponivel = saldos.TryGetValue(i.PecaId, out var saldo) ? saldo.QuantidadeDisponivel : 0;
            return new ItemDisponibilidadeResponse(i.PecaId, i.Quantidade, disponivel, disponivel >= i.Quantidade);
        }).ToList();

        return new DisponibilidadeResponse(request.FilialId, resposta.All(i => i.Atende), resposta);
    }

    public async Task<IReadOnlyList<MovimentacaoResponse>> ListarMovimentacoesAsync(FiltroMovimentacao filtro, CancellationToken ct = default)
    {
        await ExigirFilialAsync(filtro.FilialId, ct);

        var limite = Math.Clamp(filtro.Limite, 1, 500);
        var movimentacoes = await movimentacaoRepository.ListarAsync(filtro with { Limite = limite }, ct);
        return movimentacoes.Select(ToResponse).ToList();
    }

    internal static MovimentacaoResponse ToResponse(MovimentacaoEstoque m)
        => new(m.Id, m.FilialId, m.PecaId, m.Tipo.ToString(), m.Quantidade, m.Motivo, m.OsId, m.ReservaId, m.CorrelationId, m.OcorridoEm);

    private async Task ExigirFilialAsync(Guid filialId, CancellationToken ct)
    {
        if (await filialRepository.ObterPorIdAsync(filialId, ct) is null)
            throw new NotFoundException("Filial", filialId);
    }
}
