using Moq;
using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.UseCases.Estoque;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.UnitTests.UseCases.Estoque;

public class ReservaSagaUseCasesTests
{
    private readonly EstoqueFixture _f = new();
    private readonly Guid _osId = Guid.NewGuid();
    private readonly Guid _correlationId = Guid.NewGuid();

    private ReservarEstoqueUseCase Reservar => new(_f.Filiais.Object, _f.Saldos.Object, _f.Reservas.Object, _f.Movimentacoes.Object);
    private LiberarReservaUseCase Liberar => new(_f.Reservas.Object, _f.Saldos.Object, _f.Movimentacoes.Object);
    private ConsumirReservaUseCase Consumir => new(_f.Reservas.Object, _f.Saldos.Object, _f.Movimentacoes.Object, _f.Uow.Object);

    private ReservarEstoqueCommand Comando(params ItemQuantidade[] itens)
        => new(_osId, _f.Filial.Id, _correlationId, $"reserva:{_osId}", itens);

    [Fact]
    public async Task Reservar_ConfirmaSemGravar()
    {
        _f.ComSaldo(_f.Filtro, 5);
        _f.ComSaldo(_f.Oleo, 10);

        var resultado = await Reservar.ExecutarAsync(Comando(new ItemQuantidade(_f.Filtro.Id, 1), new ItemQuantidade(_f.Oleo.Id, 4)));

        var confirmada = Assert.IsType<ReservaConfirmada>(resultado);
        Assert.Equal(2, confirmada.Itens.Count);
        Assert.Equal(confirmada.ReservaId, Assert.Single(_f.ReservasAdicionadas).Id);
        Assert.Equal(2, _f.MovimentacoesAdicionadas.Count);
        // Quem grava é o handler, junto com a outbox.
        _f.Uow.Verify(u => u.SalvarAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reservar_MesmaChaveDevolveAMesmaReservaSemReservarDeNovo()
    {
        _f.ComSaldo(_f.Filtro, 5);
        var primeira = (ReservaConfirmada)await Reservar.ExecutarAsync(Comando(new ItemQuantidade(_f.Filtro.Id, 2)));

        var segunda = Assert.IsType<ReservaConfirmada>(await Reservar.ExecutarAsync(Comando(new ItemQuantidade(_f.Filtro.Id, 2))));

        Assert.Equal(primeira.ReservaId, segunda.ReservaId);
        Assert.Single(_f.ReservasAdicionadas);
        Assert.Equal(2, _f.SaldosDaFilial[_f.Filtro.Id].QuantidadeReservada);
    }

    [Fact]
    public async Task Reservar_SaldoInsuficiente_RecusaComPecasESemMudanca()
    {
        _f.ComSaldo(_f.Filtro, 5);
        _f.ComSaldo(_f.Oleo, 1);

        var resultado = await Reservar.ExecutarAsync(Comando(new ItemQuantidade(_f.Filtro.Id, 1), new ItemQuantidade(_f.Oleo.Id, 4)));

        var recusada = Assert.IsType<ReservaRecusada>(resultado);
        Assert.Equal([_f.Oleo.Id], recusada.PecasIndisponiveis);
        Assert.Empty(_f.ReservasAdicionadas);
        Assert.Empty(_f.MovimentacoesAdicionadas);
        Assert.Equal(5, _f.SaldosDaFilial[_f.Filtro.Id].QuantidadeDisponivel);
    }

    [Fact]
    public async Task Reservar_FilialQueNaoOperaEstoque_Recusa()
    {
        var comando = Comando(new ItemQuantidade(_f.Filtro.Id, 1)) with { FilialId = Guid.NewGuid() };

        var recusada = Assert.IsType<ReservaRecusada>(await Reservar.ExecutarAsync(comando));

        Assert.Empty(recusada.PecasIndisponiveis);
    }

    [Fact]
    public async Task Reservar_FilialInativa_Recusa()
    {
        _f.ComSaldo(_f.Filtro, 5);
        _f.Filial.Desativar();

        Assert.IsType<ReservaRecusada>(await Reservar.ExecutarAsync(Comando(new ItemQuantidade(_f.Filtro.Id, 1))));
    }

    [Fact]
    public async Task Reservar_QuantidadeZero_Recusa()
    {
        _f.ComSaldo(_f.Filtro, 5);

        Assert.IsType<ReservaRecusada>(await Reservar.ExecutarAsync(Comando(new ItemQuantidade(_f.Filtro.Id, 0))));
        Assert.Empty(_f.ReservasAdicionadas);
    }

    [Fact]
    public async Task Liberar_DevolveAoDisponivelEEhIdempotente()
    {
        _f.ComSaldo(_f.Filtro, 5);
        var reserva = (ReservaConfirmada)await Reservar.ExecutarAsync(Comando(new ItemQuantidade(_f.Filtro.Id, 2)));
        _f.MovimentacoesAdicionadas.Clear();

        var primeira = await Liberar.ExecutarAsync(reserva.ReservaId, "Pagamento recusado");
        var segunda = await Liberar.ExecutarAsync(reserva.ReservaId, "Pagamento recusado");

        Assert.Equal(primeira.ItensLiberados, segunda.ItensLiberados);
        Assert.Equal(2, Assert.Single(primeira.ItensLiberados).Quantidade);
        Assert.Single(_f.MovimentacoesAdicionadas);
        Assert.Equal(5, _f.SaldosDaFilial[_f.Filtro.Id].QuantidadeDisponivel);
        _f.Uow.Verify(u => u.SalvarAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Liberar_ReservaInexistente_NotFound()
        => await Assert.ThrowsAsync<NotFoundException>(() => Liberar.ExecutarAsync(Guid.NewGuid(), "Compensação"));

    [Fact]
    public async Task ConsumoConcluido_GravaEEhIdempotente()
    {
        _f.ComSaldo(_f.Oleo, 10);
        var reserva = (ReservaConfirmada)await Reservar.ExecutarAsync(Comando(new ItemQuantidade(_f.Oleo.Id, 4)));

        await Consumir.ConcluirConsumoAsync(reserva.ReservaId, [new(_f.Oleo.Id, 3)]);
        await Consumir.ConcluirConsumoAsync(reserva.ReservaId, [new(_f.Oleo.Id, 3)]);

        var saldo = _f.SaldosDaFilial[_f.Oleo.Id];
        Assert.Equal(7, saldo.QuantidadeDisponivel);
        Assert.Equal(0, saldo.QuantidadeReservada);
        _f.Uow.Verify(u => u.SalvarAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsumoComFalha_MantemRestanteReservadoEEhIdempotente()
    {
        _f.ComSaldo(_f.Oleo, 10);
        var reserva = (ReservaConfirmada)await Reservar.ExecutarAsync(Comando(new ItemQuantidade(_f.Oleo.Id, 4)));

        await Consumir.RegistrarConsumoComFalhaAsync(reserva.ReservaId, [new(_f.Oleo.Id, 1)]);
        await Consumir.RegistrarConsumoComFalhaAsync(reserva.ReservaId, [new(_f.Oleo.Id, 1)]);

        Assert.Equal(3, _f.SaldosDaFilial[_f.Oleo.Id].QuantidadeReservada);
        _f.Uow.Verify(u => u.SalvarAsync(It.IsAny<CancellationToken>()), Times.Once);

        var liberada = await Liberar.ExecutarAsync(reserva.ReservaId, "Execução falhou");
        Assert.Equal(3, Assert.Single(liberada.ItensLiberados).Quantidade);
        Assert.Equal(9, _f.SaldosDaFilial[_f.Oleo.Id].QuantidadeDisponivel);
    }

    [Fact]
    public async Task Consumo_ReservaInexistente_NotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Consumir.ConcluirConsumoAsync(Guid.NewGuid(), []));
        await Assert.ThrowsAsync<NotFoundException>(() => Consumir.RegistrarConsumoComFalhaAsync(Guid.NewGuid(), []));
    }
}
