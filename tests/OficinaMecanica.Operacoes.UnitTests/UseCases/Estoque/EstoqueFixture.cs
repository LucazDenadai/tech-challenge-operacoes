using Moq;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Catalogo;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.UnitTests.UseCases.Estoque;

// Repositórios simulados sobre objetos reais do domínio, para os testes conferirem saldos de verdade.
public class EstoqueFixture
{
    public Mock<IFilialEstoqueRepository> Filiais { get; } = new();
    public Mock<IPecaRepository> Pecas { get; } = new();
    public Mock<ISaldoEstoqueRepository> Saldos { get; } = new();
    public Mock<IReservaRepository> Reservas { get; } = new();
    public Mock<IMovimentacaoRepository> Movimentacoes { get; } = new();
    public Mock<IUnidadeDeTrabalho> Uow { get; } = new();

    public FilialEstoque Filial { get; } = new(Guid.NewGuid(), "FILIAL-DEMO");
    public Peca Filtro { get; } = new("FLT-01", "Filtro de óleo", "", 45.90m);
    public Peca Oleo { get; } = new("OLE-5W30", "Óleo 5W30", "", 39.90m);

    public Dictionary<Guid, SaldoEstoque> SaldosDaFilial { get; } = new();
    public List<MovimentacaoEstoque> MovimentacoesAdicionadas { get; } = new();
    public List<Reserva> ReservasAdicionadas { get; } = new();

    public EstoqueFixture()
    {
        Filiais.Setup(r => r.ObterPorIdAsync(Filial.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Filial);
        foreach (var peca in new[] { Filtro, Oleo })
            Pecas.Setup(r => r.ObterPorIdAsync(peca.Id, It.IsAny<CancellationToken>())).ReturnsAsync(peca);
        Pecas.Setup(r => r.ObterPorIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Guid> ids, CancellationToken _) => new[] { Filtro, Oleo }.Where(p => ids.Contains(p.Id)).ToList());

        Saldos.Setup(r => r.ObterAsync(Filial.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid pecaId, CancellationToken _) => SaldosDaFilial.GetValueOrDefault(pecaId));
        Saldos.Setup(r => r.ObterPorPecasAsync(Filial.Id, It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IEnumerable<Guid> ids, CancellationToken _) =>
                (IReadOnlyDictionary<Guid, SaldoEstoque>)SaldosDaFilial.Where(s => ids.Contains(s.Key)).ToDictionary());
        Saldos.Setup(r => r.ListarPorFilialAsync(Filial.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => SaldosDaFilial.Values.ToList());
        Saldos.Setup(r => r.AdicionarAsync(It.IsAny<SaldoEstoque>(), It.IsAny<CancellationToken>()))
            .Callback((SaldoEstoque s, CancellationToken _) => SaldosDaFilial[s.PecaId] = s);

        Movimentacoes.Setup(r => r.AdicionarAsync(It.IsAny<IEnumerable<MovimentacaoEstoque>>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<MovimentacaoEstoque> m, CancellationToken _) => MovimentacoesAdicionadas.AddRange(m));
        Reservas.Setup(r => r.AdicionarAsync(It.IsAny<Reserva>(), It.IsAny<CancellationToken>()))
            .Callback((Reserva r, CancellationToken _) =>
            {
                ReservasAdicionadas.Add(r);
                Reservas.Setup(x => x.ObterPorIdAsync(r.Id, It.IsAny<CancellationToken>())).ReturnsAsync(r);
                Reservas.Setup(x => x.ObterPorIdempotencyKeyAsync(r.IdempotencyKey, It.IsAny<CancellationToken>())).ReturnsAsync(r);
            });
    }

    public SaldoEstoque ComSaldo(Peca peca, int quantidade)
    {
        var saldo = new SaldoEstoque(Filial.Id, peca.Id);
        if (quantidade > 0)
            saldo.RegistrarEntrada(quantidade, "Estoque inicial");
        SaldosDaFilial[peca.Id] = saldo;
        return saldo;
    }
}
