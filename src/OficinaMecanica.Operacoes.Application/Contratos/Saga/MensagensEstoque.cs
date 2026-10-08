namespace OficinaMecanica.Operacoes.Application.Contratos.Saga;

// Mensagens do módulo Estoque no asyncapi-saga-os.yaml (ADR-018).

// Comandos do OS consumidos por Operações.

public sealed record InventoryReservationRequested : ComandoSaga
{
    public required IReadOnlyList<PecaQuantity> Items { get; init; }

    protected override void ValidarPayload(List<string> erros)
    {
        base.ValidarPayload(erros);
        if (Items.Count < 1)
            erros.Add("items deve ter ao menos um item.");
        ValidarPecas(erros, Items, "items");
    }
}

public sealed record InventoryReleaseRequested : ComandoSaga
{
    public required string Reason { get; init; }
    public required Guid ReservationId { get; init; }

    protected override void ValidarPayload(List<string> erros)
    {
        base.ValidarPayload(erros);
        ExigirTexto(erros, Reason, "reason");
    }
}

// Eventos publicados por Operações para o OS.

public sealed record InventoryReserved : MensagemSaga
{
    public required Guid ReservationId { get; init; }
    public required IReadOnlyList<PecaQuantity> Items { get; init; }

    protected override void ValidarPayload(List<string> erros) => ValidarPecas(erros, Items, "items");
}

public sealed record InventoryReservationRejected : Rejection
{
    public IReadOnlyList<Guid>? UnavailablePecaIds { get; init; }
}

public sealed record InventoryReleased : MensagemSaga
{
    public required Guid ReservationId { get; init; }
    public required IReadOnlyList<PecaQuantity> ReleasedItems { get; init; }

    protected override void ValidarPayload(List<string> erros) => ValidarPecas(erros, ReleasedItems, "releasedItems");
}
