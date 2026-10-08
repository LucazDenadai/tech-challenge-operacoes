namespace OficinaMecanica.Operacoes.Application.Contratos.Saga;

// Mensagens do módulo Execução no asyncapi-saga-os.yaml (ADR-018).

public sealed record PricedItem
{
    private static readonly string[] _tipos = ["Peca", "Servico"];

    public required Guid ItemId { get; init; }
    public required string Type { get; init; }
    public required string Description { get; init; }
    public required int Quantity { get; init; }
    public required decimal UnitPrice { get; init; }

    internal void Validar(List<string> erros, string campo)
    {
        if (!_tipos.Contains(Type))
            erros.Add($"{campo}.type '{Type}' não pertence ao contrato.");
        if (string.IsNullOrEmpty(Description))
            erros.Add($"{campo}.description não pode ser vazio.");
        if (Quantity < 1)
            erros.Add($"{campo}.quantity deve ser maior ou igual a 1.");
        if (UnitPrice <= 0 || decimal.Round(UnitPrice, 2) != UnitPrice)
            erros.Add($"{campo}.unitPrice deve ser positivo com no máximo duas casas decimais.");
    }
}

// Comandos do OS consumidos por Operações.

public sealed record DiagnosisRequested : ComandoSaga
{
    public required Guid VeiculoId { get; init; }
}

public sealed record ExecutionStartRequested : ComandoSaga
{
    public required Guid ExecutionId { get; init; }
    public required Guid ReservationId { get; init; }
}

// Eventos publicados por Operações para o OS.

public sealed record DiagnosisCompleted : MensagemSaga
{
    public required Guid ExecutionId { get; init; }
    public required DateTimeOffset PriceSnapshotAtUtc { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyList<PricedItem> Items { get; init; }

    protected override void ValidarPayload(List<string> erros)
    {
        ExigirMoeda(erros, Currency);
        if (Items.Count < 1)
            erros.Add("items deve ter ao menos um item.");
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i] is null)
                erros.Add($"items[{i}] não pode ser nulo.");
            else
                Items[i].Validar(erros, $"items[{i}]");
        }
    }
}

public sealed record DiagnosisRejected : Rejection;

public sealed record ExecutionStarted : MensagemSaga
{
    public required Guid ExecutionId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
}

public sealed record ExecutionStartRejected : Rejection
{
    public required Guid ExecutionId { get; init; }
}

public sealed record ExecutionCompleted : MensagemSaga
{
    public required Guid ExecutionId { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required IReadOnlyList<PecaQuantity> ConsumedItems { get; init; }

    protected override void ValidarPayload(List<string> erros) => ValidarPecas(erros, ConsumedItems, "consumedItems");
}

public sealed record ExecutionFailed : MensagemSaga
{
    public required string Reason { get; init; }
    public required Guid ExecutionId { get; init; }
    public required IReadOnlyList<PecaQuantity> ConsumedItems { get; init; }

    protected override void ValidarPayload(List<string> erros)
    {
        ExigirTexto(erros, Reason, "reason");
        ValidarPecas(erros, ConsumedItems, "consumedItems");
    }
}
