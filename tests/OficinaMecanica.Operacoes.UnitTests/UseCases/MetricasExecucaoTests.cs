using System.Diagnostics.Metrics;
using OficinaMecanica.Operacoes.Application.UseCases.Execucoes;
using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.UnitTests.UseCases;

// Tempo na fila e duração do reparo (CARD-42a).
public class MetricasExecucaoTests
{
    private const string Mecanico = "mecanico@oficina.example";

    private static Execucao NaFila()
    {
        var execucao = Execucao.Abrir(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        execucao.RegistrarDiagnostico([new(Guid.NewGuid(), TipoItemDiagnostico.Servico, "Alinhamento", 1, 80m)], Mecanico);
        execucao.Enfileirar(null, Guid.NewGuid(), "inicio:1");
        return execucao;
    }

    [Fact]
    public void Reparo_RegistraTempoNaFilaEDuracaoComResultado()
    {
        var medidas = Ouvir();
        var execucao = NaFila();

        execucao.IniciarReparo(Mecanico);
        MetricasExecucao.ReparoIniciado(execucao);
        execucao.Concluir([], Mecanico);
        MetricasExecucao.ReparoEncerrado(execucao);

        Assert.Single(medidas, m => m.Instrumento == MetricasExecucao.NomeTempoNaFila && m.Valor >= 0);
        Assert.Single(medidas, m => m.Instrumento == MetricasExecucao.NomeDuracaoReparo && m.Resultado == nameof(StatusExecucao.Concluida));
    }

    [Fact]
    public void FalhaAindaNaFila_NaoEntraNaDuracaoDoReparo()
    {
        var medidas = Ouvir();
        var execucao = NaFila();

        execucao.Falhar("Peça errada.", [], Mecanico);
        MetricasExecucao.ReparoEncerrado(execucao);

        Assert.DoesNotContain(medidas, m => m.Instrumento == MetricasExecucao.NomeDuracaoReparo);
    }

    // As medidas de outros testes em paralelo também chegam aqui; cada teste filtra pelo que espera.
    private static List<(string Instrumento, double Valor, string? Resultado)> Ouvir()
    {
        var medidas = new List<(string, double, string?)>();
        var ouvinte = new MeterListener
        {
            InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == MetricasExecucao.NomeMedidor)
                    l.EnableMeasurementEvents(i);
            }
        };
        ouvinte.SetMeasurementEventCallback<double>((i, valor, rotulos, _) =>
        {
            string? resultado = null;
            foreach (var r in rotulos)
                if (r.Key == "resultado")
                    resultado = r.Value?.ToString();
            lock (medidas)
                medidas.Add((i.Name, valor, resultado));
        });
        ouvinte.Start();
        return medidas;
    }
}
