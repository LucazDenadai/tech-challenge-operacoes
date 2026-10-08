using Moq;
using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Application.UseCases.Estoque;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.UnitTests.UseCases.Estoque;

public class ConsultarEMovimentarEstoqueTests
{
    private readonly EstoqueFixture _f = new();

    private ConsultarEstoqueUseCase Consultar => new(_f.Filiais.Object, _f.Saldos.Object, _f.Pecas.Object, _f.Movimentacoes.Object);
    private MovimentarEstoqueUseCase Movimentar => new(_f.Filiais.Object, _f.Pecas.Object, _f.Saldos.Object, _f.Movimentacoes.Object, _f.Uow.Object);

    [Fact]
    public async Task Entrada_CriaSaldoQuandoNaoExisteERegistraMovimentacao()
    {
        var resposta = await Movimentar.RegistrarEntradaAsync(new EntradaEstoqueRequest
        {
            FilialId = _f.Filial.Id, PecaId = _f.Filtro.Id, Quantidade = 8, Motivo = "Compra NF 123"
        });

        Assert.Equal(8, resposta.QuantidadeDisponivel);
        Assert.Equal("FLT-01", resposta.CodigoPeca);
        var mov = Assert.Single(_f.MovimentacoesAdicionadas);
        Assert.Equal(TipoMovimentacao.Entrada, mov.Tipo);
        _f.Saldos.Verify(r => r.AdicionarAsync(It.IsAny<SaldoEstoque>(), It.IsAny<CancellationToken>()), Times.Once);
        _f.Uow.Verify(u => u.SalvarAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Entrada_SomaAoSaldoExistente()
    {
        _f.ComSaldo(_f.Filtro, 2);

        var resposta = await Movimentar.RegistrarEntradaAsync(new EntradaEstoqueRequest
        {
            FilialId = _f.Filial.Id, PecaId = _f.Filtro.Id, Quantidade = 3, Motivo = "Compra"
        });

        Assert.Equal(5, resposta.QuantidadeDisponivel);
        _f.Saldos.Verify(r => r.AdicionarAsync(It.IsAny<SaldoEstoque>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Entrada_FilialOuPecaInvalida()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Movimentar.RegistrarEntradaAsync(new EntradaEstoqueRequest
        {
            FilialId = Guid.NewGuid(), PecaId = _f.Filtro.Id, Quantidade = 1, Motivo = "Compra"
        }));
        await Assert.ThrowsAsync<NotFoundException>(() => Movimentar.RegistrarEntradaAsync(new EntradaEstoqueRequest
        {
            FilialId = _f.Filial.Id, PecaId = Guid.NewGuid(), Quantidade = 1, Motivo = "Compra"
        }));

        _f.Oleo.Desativar();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Movimentar.RegistrarEntradaAsync(new EntradaEstoqueRequest
        {
            FilialId = _f.Filial.Id, PecaId = _f.Oleo.Id, Quantidade = 1, Motivo = "Compra"
        }));

        _f.Filial.Desativar();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Movimentar.RegistrarEntradaAsync(new EntradaEstoqueRequest
        {
            FilialId = _f.Filial.Id, PecaId = _f.Filtro.Id, Quantidade = 1, Motivo = "Compra"
        }));

        _f.Uow.Verify(u => u.SalvarAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ajuste_SemDiferencaNaoGrava()
    {
        _f.ComSaldo(_f.Filtro, 4);

        var resposta = await Movimentar.AjustarAsync(new AjusteEstoqueRequest
        {
            FilialId = _f.Filial.Id, PecaId = _f.Filtro.Id, QuantidadeContada = 4, Motivo = "Inventário"
        });

        Assert.Equal(4, resposta.QuantidadeDisponivel);
        Assert.Empty(_f.MovimentacoesAdicionadas);
        _f.Uow.Verify(u => u.SalvarAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ajuste_ComDiferencaGrava()
    {
        _f.ComSaldo(_f.Filtro, 4);

        await Movimentar.AjustarAsync(new AjusteEstoqueRequest
        {
            FilialId = _f.Filial.Id, PecaId = _f.Filtro.Id, QuantidadeContada = 1, Motivo = "Inventário"
        });

        Assert.Equal(-3, Assert.Single(_f.MovimentacoesAdicionadas).Quantidade);
        _f.Uow.Verify(u => u.SalvarAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Disponibilidade_ConsolidaItensEConsideraSaldoInexistenteComoZero()
    {
        _f.ComSaldo(_f.Filtro, 3);

        var resposta = await Consultar.ConsultarDisponibilidadeAsync(new DisponibilidadeRequest
        {
            FilialId = _f.Filial.Id,
            Itens = [new() { PecaId = _f.Filtro.Id, Quantidade = 2 }, new() { PecaId = _f.Filtro.Id, Quantidade = 1 }, new() { PecaId = _f.Oleo.Id, Quantidade = 1 }]
        });

        Assert.False(resposta.Atende);
        var filtro = Assert.Single(resposta.Itens, i => i.PecaId == _f.Filtro.Id);
        Assert.Equal(3, filtro.QuantidadeSolicitada);
        Assert.True(filtro.Atende);
        var oleo = Assert.Single(resposta.Itens, i => i.PecaId == _f.Oleo.Id);
        Assert.Equal(0, oleo.QuantidadeDisponivel);
        Assert.False(oleo.Atende);
    }

    [Fact]
    public async Task Saldos_ListaComDadosDaPeca()
    {
        _f.ComSaldo(_f.Oleo, 10);
        _f.ComSaldo(_f.Filtro, 3);

        var saldos = await Consultar.ListarSaldosAsync(_f.Filial.Id);

        Assert.Equal(["FLT-01", "OLE-5W30"], saldos.Select(s => s.CodigoPeca));
    }

    [Fact]
    public async Task Consultas_FilialInexistente_NotFound()
    {
        var outra = Guid.NewGuid();

        await Assert.ThrowsAsync<NotFoundException>(() => Consultar.ListarSaldosAsync(outra));
        await Assert.ThrowsAsync<NotFoundException>(() => Consultar.ListarMovimentacoesAsync(new FiltroMovimentacao(outra)));
    }

    [Fact]
    public async Task Movimentacoes_LimitaQuantidadeDeRegistros()
    {
        _f.Movimentacoes.Setup(r => r.ListarAsync(It.IsAny<FiltroMovimentacao>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await Consultar.ListarMovimentacoesAsync(new FiltroMovimentacao(_f.Filial.Id, Limite: 10_000));

        _f.Movimentacoes.Verify(r => r.ListarAsync(It.Is<FiltroMovimentacao>(f => f.Limite == 500), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Filiais_Lista()
    {
        _f.Filiais.Setup(r => r.ListarAsync(It.IsAny<CancellationToken>())).ReturnsAsync([_f.Filial]);

        var filial = Assert.Single(await Consultar.ListarFiliaisAsync());
        Assert.Equal("FILIAL-DEMO", filial.Codigo);
    }
}
