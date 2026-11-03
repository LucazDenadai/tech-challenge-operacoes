using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.UnitTests.Dominio;

public class SaldoEstoqueTests
{
    private readonly Guid _filialId = Guid.NewGuid();
    private readonly Guid _pecaId = Guid.NewGuid();

    [Fact]
    public void Entrada_SomaAoDisponivelERegistraMovimentacao()
    {
        var saldo = new SaldoEstoque(_filialId, _pecaId);

        var mov = saldo.RegistrarEntrada(10, "Compra");

        Assert.Equal(10, saldo.QuantidadeDisponivel);
        Assert.Equal(TipoMovimentacao.Entrada, mov.Tipo);
        Assert.Equal(10, mov.Quantidade);
        Assert.Equal(_filialId, mov.FilialId);
        Assert.Equal(_pecaId, mov.PecaId);
        Assert.Null(mov.OsId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Entrada_RecusaQuantidadeNaoPositiva(int quantidade)
        => Assert.Throws<ArgumentException>(() => new SaldoEstoque(_filialId, _pecaId).RegistrarEntrada(quantidade, "Compra"));

    [Fact]
    public void Entrada_ExigeMotivo()
        => Assert.Throws<ArgumentException>(() => new SaldoEstoque(_filialId, _pecaId).RegistrarEntrada(1, " "));

    [Fact]
    public void Ajuste_GuardaDiferencaComSinal()
    {
        var saldo = new SaldoEstoque(_filialId, _pecaId);
        saldo.RegistrarEntrada(10, "Compra");

        var mov = saldo.Ajustar(7, "Inventário");

        Assert.Equal(7, saldo.QuantidadeDisponivel);
        Assert.Equal(TipoMovimentacao.Ajuste, mov!.Tipo);
        Assert.Equal(-3, mov.Quantidade);
    }

    [Fact]
    public void Ajuste_SemDiferencaNaoGeraMovimentacao()
    {
        var saldo = new SaldoEstoque(_filialId, _pecaId);
        saldo.RegistrarEntrada(5, "Compra");

        Assert.Null(saldo.Ajustar(5, "Inventário"));
    }

    [Fact]
    public void Ajuste_RecusaQuantidadeNegativa()
        => Assert.Throws<ArgumentException>(() => new SaldoEstoque(_filialId, _pecaId).Ajustar(-1, "Inventário"));

    [Fact]
    public void Construtor_ExigeFilialEPeca()
    {
        Assert.Throws<ArgumentException>(() => new SaldoEstoque(Guid.Empty, _pecaId));
        Assert.Throws<ArgumentException>(() => new SaldoEstoque(_filialId, Guid.Empty));
    }

    [Fact]
    public void PodeReservar_SoComSaldoSuficiente()
    {
        var saldo = new SaldoEstoque(_filialId, _pecaId);
        saldo.RegistrarEntrada(2, "Compra");

        Assert.True(saldo.PodeReservar(2));
        Assert.False(saldo.PodeReservar(3));
        Assert.False(saldo.PodeReservar(0));
    }

    // Guardas internas: a Reserva confere antes de chamar, mas o saldo não depende disso para nunca ficar negativo.
    [Fact]
    public void Reservar_AlemDoDisponivel_RecusadoSemAlterarSaldo()
    {
        var saldo = new SaldoEstoque(_filialId, _pecaId);
        saldo.RegistrarEntrada(2, "Compra");

        Assert.Throws<InvalidOperationException>(() => saldo.Reservar(3, new ReferenciaSaga(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())));
        Assert.Equal(2, saldo.QuantidadeDisponivel);
        Assert.Equal(0, saldo.QuantidadeReservada);
    }

    [Fact]
    public void ConsumirOuLiberar_AlemDoReservado_Recusado()
    {
        var referencia = new ReferenciaSaga(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var saldo = new SaldoEstoque(_filialId, _pecaId);
        saldo.RegistrarEntrada(5, "Compra");
        saldo.Reservar(1, referencia);

        Assert.Throws<InvalidOperationException>(() => saldo.Consumir(2, referencia));
        Assert.Throws<InvalidOperationException>(() => saldo.Liberar(2, "Sobra", referencia));
        Assert.Equal(1, saldo.QuantidadeReservada);
    }
}
