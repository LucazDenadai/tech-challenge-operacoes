using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.UnitTests.Dominio;

public class ReservaTests
{
    private readonly Guid _filialId = Guid.NewGuid();
    private readonly Guid _osId = Guid.NewGuid();
    private readonly Guid _correlationId = Guid.NewGuid();
    private readonly Guid _filtro = Guid.NewGuid();
    private readonly Guid _oleo = Guid.NewGuid();

    private Dictionary<Guid, SaldoEstoque> Saldos(int filtro, int oleo)
    {
        var saldos = new Dictionary<Guid, SaldoEstoque>();
        foreach (var (peca, quantidade) in new[] { (_filtro, filtro), (_oleo, oleo) })
        {
            var saldo = new SaldoEstoque(_filialId, peca);
            if (quantidade > 0)
                saldo.RegistrarEntrada(quantidade, "Estoque inicial");
            saldos[peca] = saldo;
        }
        return saldos;
    }

    private (Reserva Reserva, IReadOnlyList<MovimentacaoEstoque> Movs) Reservar(Dictionary<Guid, SaldoEstoque> saldos, params ItemQuantidade[] itens)
        => Reserva.Reservar(_osId, _filialId, _correlationId, "reserva-os-1", itens, saldos);

    [Fact]
    public void Reservar_MoveDoDisponivelParaReservadoComReferencia()
    {
        var saldos = Saldos(filtro: 5, oleo: 10);

        var (reserva, movs) = Reservar(saldos, new ItemQuantidade(_filtro, 1), new ItemQuantidade(_oleo, 4));

        Assert.Equal(StatusReserva.Ativa, reserva.Status);
        Assert.Equal(4, saldos[_filtro].QuantidadeDisponivel);
        Assert.Equal(1, saldos[_filtro].QuantidadeReservada);
        Assert.Equal(6, saldos[_oleo].QuantidadeDisponivel);
        Assert.Equal(4, saldos[_oleo].QuantidadeReservada);
        Assert.All(movs, m =>
        {
            Assert.Equal(TipoMovimentacao.Reserva, m.Tipo);
            Assert.Equal(_osId, m.OsId);
            Assert.Equal(reserva.Id, m.ReservaId);
            Assert.Equal(_correlationId, m.CorrelationId);
        });
    }

    [Fact]
    public void Reservar_ConsolidaPecaRepetida()
    {
        var saldos = Saldos(filtro: 5, oleo: 0);

        var (reserva, _) = Reservar(saldos, new ItemQuantidade(_filtro, 1), new ItemQuantidade(_filtro, 2));

        var item = Assert.Single(reserva.Itens);
        Assert.Equal(3, item.Quantidade);
        Assert.Equal(3, saldos[_filtro].QuantidadeReservada);
    }

    [Fact]
    public void Reservar_TudoOuNada_QuandoFaltaUmaPecaNenhumSaldoMuda()
    {
        var saldos = Saldos(filtro: 5, oleo: 1);

        var ex = Assert.Throws<EstoqueInsuficienteException>(() => Reservar(saldos, new ItemQuantidade(_filtro, 1), new ItemQuantidade(_oleo, 4)));

        Assert.Equal([_oleo], ex.PecasIndisponiveis);
        Assert.Equal(5, saldos[_filtro].QuantidadeDisponivel);
        Assert.Equal(0, saldos[_filtro].QuantidadeReservada);
        Assert.Equal(1, saldos[_oleo].QuantidadeDisponivel);
    }

    [Fact]
    public void Reservar_PecaSemSaldoNaFilialEhIndisponivel()
    {
        var semSaldo = Guid.NewGuid();
        var outraFilial = new SaldoEstoque(Guid.NewGuid(), _oleo);
        outraFilial.RegistrarEntrada(10, "Estoque inicial");
        var saldos = new Dictionary<Guid, SaldoEstoque> { [_oleo] = outraFilial };

        var ex = Assert.Throws<EstoqueInsuficienteException>(() => Reservar(saldos, new ItemQuantidade(semSaldo, 1), new ItemQuantidade(_oleo, 1)));

        Assert.Equal(2, ex.PecasIndisponiveis.Count);
        Assert.Equal(10, outraFilial.QuantidadeDisponivel);
    }

    [Fact]
    public void Reservar_RecusaListaVaziaOuQuantidadeNaoPositiva()
    {
        var saldos = Saldos(filtro: 5, oleo: 5);

        Assert.Throws<ArgumentException>(() => Reservar(saldos));
        Assert.Throws<ArgumentException>(() => Reservar(saldos, new ItemQuantidade(_filtro, 0)));
    }

    [Fact]
    public void Reservar_ExigeChaveDeIdempotencia()
        => Assert.Throws<ArgumentException>(() =>
            Reserva.Reservar(_osId, _filialId, _correlationId, " ", [new(_filtro, 1)], Saldos(5, 5)));

    [Fact]
    public void Concluir_ConsomeUsadoELiberaSobra()
    {
        var saldos = Saldos(filtro: 5, oleo: 10);
        var (reserva, _) = Reservar(saldos, new ItemQuantidade(_filtro, 1), new ItemQuantidade(_oleo, 4));

        var movs = reserva.Concluir([new(_filtro, 1), new(_oleo, 3)], saldos);

        Assert.Equal(StatusReserva.Consumida, reserva.Status);
        Assert.Equal(4, saldos[_filtro].QuantidadeDisponivel);
        Assert.Equal(0, saldos[_filtro].QuantidadeReservada);
        Assert.Equal(7, saldos[_oleo].QuantidadeDisponivel);
        Assert.Equal(0, saldos[_oleo].QuantidadeReservada);
        Assert.Equal(2, movs.Count(m => m.Tipo == TipoMovimentacao.Consumo));
        var liberacao = Assert.Single(movs, m => m.Tipo == TipoMovimentacao.Liberacao);
        Assert.Equal(1, liberacao.Quantidade);
    }

    [Fact]
    public void Falha_ConsomeUsadoEMantemRestanteReservadoAteLiberar()
    {
        var saldos = Saldos(filtro: 5, oleo: 10);
        var (reserva, _) = Reservar(saldos, new ItemQuantidade(_filtro, 1), new ItemQuantidade(_oleo, 4));

        reserva.RegistrarFalha([new(_oleo, 1)], saldos);

        Assert.Equal(StatusReserva.Ativa, reserva.Status);
        Assert.Equal(3, saldos[_oleo].QuantidadeReservada);
        Assert.Equal(1, saldos[_filtro].QuantidadeReservada);

        var movs = reserva.Liberar("Pagamento estornado", saldos);

        Assert.Equal(StatusReserva.Liberada, reserva.Status);
        Assert.Equal(0, saldos[_oleo].QuantidadeReservada);
        Assert.Equal(9, saldos[_oleo].QuantidadeDisponivel);
        Assert.Equal(5, saldos[_filtro].QuantidadeDisponivel);
        Assert.All(movs, m => Assert.Equal("Pagamento estornado", m.Motivo));
    }

    [Fact]
    public void Liberar_EhIdempotente()
    {
        var saldos = Saldos(filtro: 5, oleo: 0);
        var (reserva, _) = Reservar(saldos, new ItemQuantidade(_filtro, 2));

        var primeira = reserva.Liberar("Pagamento recusado", saldos);
        var segunda = reserva.Liberar("Pagamento recusado", saldos);

        Assert.Single(primeira);
        Assert.Empty(segunda);
        Assert.Equal(5, saldos[_filtro].QuantidadeDisponivel);
    }

    [Fact]
    public void Liberar_DepoisDeConcluirNaoAlteraSaldo()
    {
        var saldos = Saldos(filtro: 5, oleo: 0);
        var (reserva, _) = Reservar(saldos, new ItemQuantidade(_filtro, 2));
        reserva.Concluir([new(_filtro, 2)], saldos);

        Assert.Empty(reserva.Liberar("Compensação tardia", saldos));
        Assert.Equal(StatusReserva.Consumida, reserva.Status);
        Assert.Equal(3, saldos[_filtro].QuantidadeDisponivel);
    }

    [Fact]
    public void Consumo_ForaDaReservaEhRecusadoSemAlterarSaldo()
    {
        var saldos = Saldos(filtro: 5, oleo: 10);
        var (reserva, _) = Reservar(saldos, new ItemQuantidade(_filtro, 1));

        Assert.Throws<InvalidOperationException>(() => reserva.Concluir([new(_filtro, 2)], saldos));
        Assert.Throws<InvalidOperationException>(() => reserva.Concluir([new(_oleo, 1)], saldos));
        Assert.Throws<InvalidOperationException>(() => reserva.Concluir([new(_filtro, -1)], saldos));
        Assert.Equal(1, saldos[_filtro].QuantidadeReservada);
        Assert.Equal(StatusReserva.Ativa, reserva.Status);
    }

    [Fact]
    public void Consumo_SoPodeSerRegistradoUmaVez()
    {
        var saldos = Saldos(filtro: 5, oleo: 0);
        var (reserva, _) = Reservar(saldos, new ItemQuantidade(_filtro, 2));
        reserva.RegistrarFalha([new(_filtro, 1)], saldos);

        Assert.Throws<InvalidOperationException>(() => reserva.RegistrarFalha([new(_filtro, 1)], saldos));
        Assert.Throws<InvalidOperationException>(() => reserva.Concluir([], saldos));
    }

    [Fact]
    public void Consumo_ExigeSaldoCarregado()
    {
        var saldos = Saldos(filtro: 5, oleo: 0);
        var (reserva, _) = Reservar(saldos, new ItemQuantidade(_filtro, 2));

        Assert.Throws<InvalidOperationException>(() => reserva.Concluir([new(_filtro, 1)], new Dictionary<Guid, SaldoEstoque>()));
        Assert.Throws<InvalidOperationException>(() => reserva.RegistrarFalha([new(_filtro, 1)], new Dictionary<Guid, SaldoEstoque>()));

        Assert.False(reserva.ConsumoRegistrado);
        Assert.Equal(0, Assert.Single(reserva.Itens).QuantidadeConsumida);
        Assert.Equal(2, saldos[_filtro].QuantidadeReservada);
    }

    [Fact]
    public void Concluir_ComSaldoFaltandoDeItemNaoConsumidoNaoAlteraNada()
    {
        var saldos = Saldos(filtro: 5, oleo: 10);
        var (reserva, _) = Reservar(saldos, new ItemQuantidade(_filtro, 1), new ItemQuantidade(_oleo, 4));
        var parcial = new Dictionary<Guid, SaldoEstoque> { [_filtro] = saldos[_filtro] };

        Assert.Throws<InvalidOperationException>(() => reserva.Concluir([new(_filtro, 1)], parcial));

        Assert.False(reserva.ConsumoRegistrado);
        Assert.Equal(1, saldos[_filtro].QuantidadeReservada);
    }
}
