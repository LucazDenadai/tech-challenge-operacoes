using Moq;
using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Application.UseCases.Catalogo;
using OficinaMecanica.Operacoes.Domain.Catalogo;

namespace OficinaMecanica.Operacoes.UnitTests.UseCases.Catalogo;

public class CatalogoUseCasesTests
{
    private readonly Mock<IPecaRepository> _pecas = new();
    private readonly Mock<IServicoRepository> _servicos = new();
    private readonly Mock<IUnidadeDeTrabalho> _uow = new();

    private GerenciarPecaUseCase Pecas => new(_pecas.Object, _uow.Object);
    private GerenciarServicoUseCase Servicos => new(_servicos.Object, _uow.Object);

    [Fact]
    public async Task CriarPeca_GravaComCodigoNormalizado()
    {
        var resposta = await Pecas.CriarAsync(new CriarPecaRequest { Codigo = "flt-01", Nome = "Filtro", PrecoTabela = 45.90m });

        Assert.Equal("FLT-01", resposta.Codigo);
        Assert.Equal("BRL", resposta.Moeda);
        _pecas.Verify(r => r.AdicionarAsync(It.Is<Peca>(p => p.Codigo == "FLT-01"), default), Times.Once);
        _uow.Verify(u => u.SalvarAsync(default), Times.Once);
    }

    [Fact]
    public async Task CriarPeca_CodigoRepetido_Recusa()
    {
        _pecas.Setup(r => r.ObterPorCodigoAsync("FLT-01", default)).ReturnsAsync(new Peca("FLT-01", "Filtro", "", 10m));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pecas.CriarAsync(new CriarPecaRequest { Codigo = "flt-01", Nome = "Outro", PrecoTabela = 10m }));
        _uow.Verify(u => u.SalvarAsync(default), Times.Never);
    }

    [Fact]
    public async Task AtualizarEDesativarPeca()
    {
        var peca = new Peca("FLT-01", "Filtro", "", 10m);
        _pecas.Setup(r => r.ObterPorIdAsync(peca.Id, default)).ReturnsAsync(peca);

        var resposta = await Pecas.AtualizarAsync(peca.Id, new AtualizarPecaRequest { Nome = "Filtro novo", PrecoTabela = 12m });
        await Pecas.DesativarAsync(peca.Id);

        Assert.Equal("Filtro novo", resposta.Nome);
        Assert.False(peca.Ativo);
        _uow.Verify(u => u.SalvarAsync(default), Times.Exactly(2));
    }

    [Fact]
    public async Task ObterPeca_Inexistente_NotFound()
        => await Assert.ThrowsAsync<NotFoundException>(() => Pecas.ObterAsync(Guid.NewGuid()));

    [Fact]
    public async Task ListarPecas_RepassaFiltroDeInativas()
    {
        _pecas.Setup(r => r.ListarAsync(true, default)).ReturnsAsync([new Peca("A", "A", "", 1m)]);

        Assert.Single(await Pecas.ListarAsync(incluirInativas: true));
    }

    [Fact]
    public async Task CriarAtualizarEDesativarServico()
    {
        var criado = await Servicos.CriarAsync(new ServicoRequest { Nome = "Troca de óleo", Preco = 80m, TempoConclusaoMinutos = 30 });
        Assert.Equal(80m, criado.Preco);

        var servico = new Servico("Alinhamento", "", 100m, 40);
        _servicos.Setup(r => r.ObterPorIdAsync(servico.Id, default)).ReturnsAsync(servico);
        var atualizado = await Servicos.AtualizarAsync(servico.Id, new ServicoRequest { Nome = "Alinhamento 3D", Preco = 120m, TempoConclusaoMinutos = 50 });
        await Servicos.DesativarAsync(servico.Id);

        Assert.Equal("Alinhamento 3D", atualizado.Nome);
        Assert.False(servico.Ativo);
        _uow.Verify(u => u.SalvarAsync(default), Times.Exactly(3));
    }

    [Fact]
    public async Task Servico_InexistenteEListagem()
    {
        _servicos.Setup(r => r.ListarAsync(false, default)).ReturnsAsync([new Servico("A", "", 1m, 1)]);

        Assert.Single(await Servicos.ListarAsync(incluirInativos: false));
        await Assert.ThrowsAsync<NotFoundException>(() => Servicos.ObterAsync(Guid.NewGuid()));
    }
}
