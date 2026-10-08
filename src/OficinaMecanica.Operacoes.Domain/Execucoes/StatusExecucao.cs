namespace OficinaMecanica.Operacoes.Domain.Execucoes;

// Estados da execução (decisões do CARD-38b), alinhados ao diagrama de sequência da Saga:
// Diagnosing -> Diagnosed (fora da fila) -> Queued/Started -> concluída ou falha.
public enum StatusExecucao
{
    EmDiagnostico = 1,
    Diagnosticada = 2,
    DiagnosticoRejeitado = 3,
    NaFila = 4,
    EmReparo = 5,
    Concluida = 6,
    Falhou = 7
}

public enum TipoItemDiagnostico
{
    Peca = 1,
    Servico = 2
}
