namespace OficinaMecanica.Operacoes.Domain.Estoque;

// Reserva recusada. Lista as peças sem saldo, que viram unavailablePecaIds no InventoryReservationRejected.
public class EstoqueInsuficienteException : InvalidOperationException
{
    public IReadOnlyList<Guid> PecasIndisponiveis { get; }

    public EstoqueInsuficienteException(IReadOnlyList<Guid> pecasIndisponiveis)
        : base($"Saldo insuficiente na filial para {pecasIndisponiveis.Count} peça(s).")
        => PecasIndisponiveis = pecasIndisponiveis;
}
