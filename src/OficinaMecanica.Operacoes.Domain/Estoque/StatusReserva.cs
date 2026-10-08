namespace OficinaMecanica.Operacoes.Domain.Estoque;

public enum StatusReserva
{
    // Itens reservados. Pode ter consumo parcial registrado após falha da execução.
    Ativa = 1,
    // Execução concluída: o usado foi consumido e o restante voltou ao disponível.
    Consumida = 2,
    // Compensação: o que não foi consumido voltou ao disponível.
    Liberada = 3
}
