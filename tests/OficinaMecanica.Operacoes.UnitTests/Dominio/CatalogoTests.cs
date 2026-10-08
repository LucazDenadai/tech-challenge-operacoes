using OficinaMecanica.Operacoes.Domain.Catalogo;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.UnitTests.Dominio;

public class CatalogoTests
{
    [Fact]
    public void Peca_NormalizaCodigoENome()
    {
        var peca = new Peca(" flt-01 ", " Filtro de óleo ", "Filtro", 45.90m);

        Assert.Equal("FLT-01", peca.Codigo);
        Assert.Equal("Filtro de óleo", peca.Nome);
        Assert.True(peca.Ativo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.001)]
    public void Peca_RecusaPrecoForaDoContrato(decimal preco)
        => Assert.Throws<ArgumentException>(() => new Peca("FLT-01", "Filtro", "", preco));

    [Fact]
    public void Peca_RecusaCodigoOuNomeVazio()
    {
        Assert.Throws<ArgumentException>(() => new Peca(" ", "Filtro", "", 10m));
        Assert.Throws<ArgumentException>(() => new Peca("FLT-01", " ", "", 10m));
    }

    [Fact]
    public void Peca_AtualizarEDesativar()
    {
        var peca = new Peca("FLT-01", "Filtro", "", 10m);

        peca.Atualizar("Filtro novo", "Descrição", 12.50m);
        peca.Desativar();

        Assert.Equal("Filtro novo", peca.Nome);
        Assert.Equal(12.50m, peca.PrecoTabela);
        Assert.False(peca.Ativo);
        Assert.NotNull(peca.AtualizadoEm);
    }

    [Fact]
    public void Servico_ValidaPrecoETempo()
    {
        Assert.Throws<ArgumentException>(() => new Servico("Troca de óleo", "", 0m, 30));
        Assert.Throws<ArgumentException>(() => new Servico("Troca de óleo", "", 80m, 0));
        Assert.Throws<ArgumentException>(() => new Servico(" ", "", 80m, 30));
    }

    [Fact]
    public void Servico_AtualizarEDesativar()
    {
        var servico = new Servico("Troca de óleo", "", 80m, 30);

        servico.Atualizar("Troca de óleo e filtro", "Inclui filtro", 95m, 45);
        servico.Desativar();

        Assert.Equal(95m, servico.Preco);
        Assert.Equal(45, servico.TempoConclusaoMinutos);
        Assert.False(servico.Ativo);
    }

    [Fact]
    public void FilialEstoque_UsaIdDoOs()
    {
        var id = Guid.NewGuid();
        var filial = new FilialEstoque(id, "filial-demo");

        Assert.Equal(id, filial.Id);
        Assert.Equal("FILIAL-DEMO", filial.Codigo);

        filial.Desativar();
        Assert.False(filial.Ativo);
    }

    [Fact]
    public void FilialEstoque_RecusaIdVazio()
        => Assert.Throws<ArgumentException>(() => new FilialEstoque(Guid.Empty, "FILIAL-DEMO"));
}
