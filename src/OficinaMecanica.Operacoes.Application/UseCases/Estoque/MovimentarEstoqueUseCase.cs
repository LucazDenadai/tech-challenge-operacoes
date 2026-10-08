using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Catalogo;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.Application.UseCases.Estoque;

// Entrada e ajuste administrativos. Reserva, consumo e liberação vêm só da Saga.
public class MovimentarEstoqueUseCase(
    IFilialEstoqueRepository filialRepository,
    IPecaRepository pecaRepository,
    ISaldoEstoqueRepository saldoRepository,
    IMovimentacaoRepository movimentacaoRepository,
    IUnidadeDeTrabalho unidadeDeTrabalho)
{
    public async Task<SaldoResponse> RegistrarEntradaAsync(EntradaEstoqueRequest request, CancellationToken ct = default)
    {
        var (peca, saldo) = await PrepararAsync(request.FilialId, request.PecaId, ct);

        var movimentacao = saldo.RegistrarEntrada(request.Quantidade, request.Motivo);
        await movimentacaoRepository.AdicionarAsync([movimentacao], ct);
        await unidadeDeTrabalho.SalvarAsync(ct);

        return ToResponse(saldo, peca);
    }

    public async Task<SaldoResponse> AjustarAsync(AjusteEstoqueRequest request, CancellationToken ct = default)
    {
        var (peca, saldo) = await PrepararAsync(request.FilialId, request.PecaId, ct);

        var movimentacao = saldo.Ajustar(request.QuantidadeContada, request.Motivo);
        if (movimentacao is not null)
        {
            await movimentacaoRepository.AdicionarAsync([movimentacao], ct);
            await unidadeDeTrabalho.SalvarAsync(ct);
        }

        return ToResponse(saldo, peca);
    }

    private async Task<(Peca Peca, SaldoEstoque Saldo)> PrepararAsync(Guid filialId, Guid pecaId, CancellationToken ct)
    {
        var filial = await filialRepository.ObterPorIdAsync(filialId, ct) ?? throw new NotFoundException("Filial", filialId);
        if (!filial.Ativo)
            throw new InvalidOperationException($"A filial '{filial.Codigo}' está inativa.");

        var peca = await pecaRepository.ObterPorIdAsync(pecaId, ct) ?? throw new NotFoundException("Peça", pecaId);
        if (!peca.Ativo)
            throw new InvalidOperationException($"A peça '{peca.Codigo}' está inativa.");

        var saldo = await saldoRepository.ObterAsync(filialId, pecaId, ct);
        if (saldo is null)
        {
            saldo = new SaldoEstoque(filialId, pecaId);
            await saldoRepository.AdicionarAsync(saldo, ct);
        }

        return (peca, saldo);
    }

    private static SaldoResponse ToResponse(SaldoEstoque s, Peca p)
        => new(s.FilialId, s.PecaId, p.Codigo, p.Nome, s.QuantidadeDisponivel, s.QuantidadeReservada);
}
