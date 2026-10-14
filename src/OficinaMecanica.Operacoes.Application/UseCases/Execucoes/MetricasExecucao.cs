using System.Diagnostics.Metrics;
using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.Application.UseCases.Execucoes;

// Tempo na fila e duração do reparo (CARD-42a): só Operações sabe disso. Os eventos de execução e de reserva já
// são contados pelo OS, que os recebe (os_saga_mensagens por tipo e resultado).
public static class MetricasExecucao
{
    public const string NomeMedidor = "OficinaMecanica.Operacoes.Execucao";
    public const string NomeTempoNaFila = "operacoes_tempo_na_fila_segundos";
    public const string NomeDuracaoReparo = "operacoes_duracao_reparo_segundos";

    private static readonly Meter _medidor = new(NomeMedidor);
    private static readonly Histogram<double> _tempoNaFila = _medidor.CreateHistogram<double>(
        NomeTempoNaFila, description: "Tempo entre a entrada na fila e o início do reparo.");
    private static readonly Histogram<double> _duracaoReparo = _medidor.CreateHistogram<double>(
        NomeDuracaoReparo, description: "Tempo entre o início do reparo e a conclusão ou falha, por resultado.");

    public static void ReparoIniciado(Execucao execucao)
    {
        if (execucao.EnfileiradaEm is { } entrada && InicioDoReparo(execucao) is { } inicio)
            _tempoNaFila.Record(Segundos(inicio - entrada));
    }

    // Falha ainda na fila não teve reparo: não entra na duração.
    public static void ReparoEncerrado(Execucao execucao)
    {
        if (InicioDoReparo(execucao) is { } inicio && execucao.EncerradaEm is { } fim)
            _duracaoReparo.Record(Segundos(fim - inicio), new KeyValuePair<string, object?>("resultado", execucao.Status.ToString()));
    }

    private static DateTime? InicioDoReparo(Execucao execucao)
        => execucao.Historico.LastOrDefault(t => t.Para == StatusExecucao.EmReparo)?.Em;

    private static double Segundos(TimeSpan duracao) => Math.Max(0, duracao.TotalSeconds);
}
