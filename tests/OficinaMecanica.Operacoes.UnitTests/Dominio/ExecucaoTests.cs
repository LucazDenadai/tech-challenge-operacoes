using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.UnitTests.Dominio;

public class ExecucaoTests
{
    private readonly Guid _oleo = Guid.NewGuid();
    private readonly Guid _filtro = Guid.NewGuid();
    private readonly Guid _servico = Guid.NewGuid();

    private Execucao Nova() => Execucao.Abrir(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private IReadOnlyList<ItemDiagnostico> Itens() =>
    [
        new(_oleo, TipoItemDiagnostico.Peca, "Óleo 5W30", 4, 39.90m),
        new(_filtro, TipoItemDiagnostico.Peca, "Filtro de óleo", 1, 45.90m),
        new(_servico, TipoItemDiagnostico.Servico, "Troca de óleo", 1, 80.00m)
    ];

    private Execucao EmReparo()
    {
        var execucao = Nova();
        execucao.RegistrarDiagnostico(Itens(), "mecanico@oficina.example");
        execucao.Enfileirar(Guid.NewGuid(), Guid.NewGuid(), "inicio:1");
        execucao.IniciarReparo("mecanico@oficina.example");
        return execucao;
    }

    [Fact]
    public void Abrir_ComecaEmDiagnosticoComHistorico()
    {
        var execucao = Nova();

        Assert.Equal(StatusExecucao.EmDiagnostico, execucao.Status);
        var inicio = Assert.Single(execucao.Historico);
        Assert.Null(inicio.De);
        Assert.Equal(Execucao.Sistema, inicio.Responsavel);
    }

    [Fact]
    public void Abrir_ExigeOsEFilial()
    {
        Assert.Throws<ArgumentException>(() => Execucao.Abrir(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => Execucao.Abrir(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void CicloCompleto_RegistraCadaTransicaoComResponsavel()
    {
        var execucao = EmReparo();
        execucao.RegistrarEtapa("Óleo drenado", "mecanico@oficina.example");

        execucao.Concluir([new(_oleo, 3), new(_filtro, 1)], "mecanico@oficina.example");

        Assert.Equal(StatusExecucao.Concluida, execucao.Status);
        Assert.NotNull(execucao.EncerradaEm);
        Assert.Single(execucao.Etapas);
        Assert.Equal(
            [StatusExecucao.EmDiagnostico, StatusExecucao.Diagnosticada, StatusExecucao.NaFila, StatusExecucao.EmReparo, StatusExecucao.Concluida],
            execucao.Historico.Select(h => h.Para));
        Assert.Equal(Execucao.Sistema, execucao.Historico.Single(h => h.Para == StatusExecucao.NaFila).Responsavel);
        Assert.Equal(2, execucao.Consumidas.Count);
    }

    [Fact]
    public void Diagnostico_RecusaListaVaziaItemInvalidoOuRepetido()
    {
        var execucao = Nova();

        Assert.Throws<ArgumentException>(() => execucao.RegistrarDiagnostico([], "m"));
        Assert.Throws<ArgumentException>(() => execucao.RegistrarDiagnostico([new(_oleo, TipoItemDiagnostico.Peca, "Óleo", 0, 10m)], "m"));
        Assert.Throws<ArgumentException>(() => execucao.RegistrarDiagnostico([new(_oleo, TipoItemDiagnostico.Peca, "Óleo", 1, 10.001m)], "m"));
        Assert.Throws<ArgumentException>(() => execucao.RegistrarDiagnostico(
            [new(_oleo, TipoItemDiagnostico.Peca, "Óleo", 1, 10m), new(_oleo, TipoItemDiagnostico.Peca, "Óleo", 2, 10m)], "m"));
        Assert.Equal(StatusExecucao.EmDiagnostico, execucao.Status);
    }

    [Fact]
    public void RejeitarDiagnostico_EncerraComMotivo()
    {
        var execucao = Nova();

        execucao.RejeitarDiagnostico("Veículo sem conserto viável", "mecanico@oficina.example");

        Assert.Equal(StatusExecucao.DiagnosticoRejeitado, execucao.Status);
        Assert.Equal("Veículo sem conserto viável", execucao.Motivo);
        Assert.Throws<ArgumentException>(() => Nova().RejeitarDiagnostico(" ", "m"));
    }

    [Fact]
    public void TransicoesForaDeOrdem_SaoRecusadas()
    {
        var emDiagnostico = Nova();
        Assert.Throws<InvalidOperationException>(() => emDiagnostico.Enfileirar(Guid.NewGuid(), Guid.NewGuid(), "k"));
        Assert.Throws<InvalidOperationException>(() => emDiagnostico.IniciarReparo("m"));
        Assert.Throws<InvalidOperationException>(() => emDiagnostico.RegistrarEtapa("x", "m"));
        Assert.Throws<InvalidOperationException>(() => emDiagnostico.Concluir([], "m"));
        Assert.Throws<InvalidOperationException>(() => emDiagnostico.Falhar("x", [], "m"));

        var concluida = EmReparo();
        concluida.Concluir([], "m");
        Assert.Throws<InvalidOperationException>(() => concluida.Falhar("x", [], "m"));
        Assert.Throws<InvalidOperationException>(() => concluida.RegistrarDiagnostico(Itens(), "m"));
    }

    [Fact]
    public void Enfileirar_ExigeReserva()
    {
        var execucao = Nova();
        execucao.RegistrarDiagnostico(Itens(), "m");

        Assert.Throws<ArgumentException>(() => execucao.Enfileirar(Guid.Empty, Guid.NewGuid(), "k"));
        Assert.Equal(StatusExecucao.Diagnosticada, execucao.Status);
    }

    [Fact]
    public void Consumo_SoDePecasDiagnosticadasAteAQuantidade()
    {
        var execucao = EmReparo();

        Assert.Throws<InvalidOperationException>(() => execucao.ValidarConclusao([new(Guid.NewGuid(), 1)]));
        Assert.Throws<InvalidOperationException>(() => execucao.ValidarConclusao([new(_servico, 1)]));
        Assert.Throws<InvalidOperationException>(() => execucao.ValidarConclusao([new(_oleo, 3), new(_oleo, 2)]));
        Assert.Throws<ArgumentException>(() => execucao.ValidarConclusao([new(_oleo, -1)]));
        Assert.Equal(StatusExecucao.EmReparo, execucao.Status);
    }

    [Fact]
    public void Falha_NaFilaOuEmReparo_ComMotivo()
    {
        var naFila = Nova();
        naFila.RegistrarDiagnostico(Itens(), "m");
        naFila.Enfileirar(Guid.NewGuid(), Guid.NewGuid(), "k");
        naFila.Falhar("Mecânico indisponível", [], "admin@oficina.example");
        Assert.Equal(StatusExecucao.Falhou, naFila.Status);

        var emReparo = EmReparo();
        Assert.Throws<ArgumentException>(() => emReparo.Falhar(" ", [], "m"));
        emReparo.Falhar("Peça danificada na instalação", [new(_oleo, 1)], "m");
        Assert.Equal(1, Assert.Single(emReparo.Consumidas).Quantidade);
    }

    [Fact]
    public void Reidratar_PreservaEstado()
    {
        var original = EmReparo();
        original.RegistrarEtapa("Óleo drenado", "m");
        original.MarcarGravada(4);

        var copia = Execucao.Reidratar(original.Estado());

        Assert.Equivalent(original.Estado(), copia.Estado());
        copia.Concluir([new(_oleo, 4)], "m");
        Assert.Equal(StatusExecucao.Concluida, copia.Status);
    }
}
