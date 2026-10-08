using System.Text.Json;
using System.Text.Json.Serialization;

namespace OficinaMecanica.Operacoes.Application.Contratos.Saga;

// De onde vem o envelope de um evento: correlação, OS e filial da Saga; causationId = messageId do comando
// que levou ao evento (ADR-017).
public readonly record struct OrigemEvento(Guid CorrelationId, Guid? CausationId, Guid OsId, Guid FilialId)
{
    public static OrigemEvento De(MensagemSaga comando) => new(comando.CorrelationId, comando.MessageId, comando.OsId, comando.FilialId);
}

// Monta e serializa os eventos que Operações publica.
public static class RespostaSaga
{
    private static readonly JsonSerializerOptions _opcoes = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── Estoque ───────────────────────────────────────────────────────────────

    public static InventoryReserved Reservado(MensagemSaga comando, Guid reservationId, IReadOnlyList<PecaQuantity> itens) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = Tipo<InventoryReserved>(), SchemaVersion = 1, Producer = CatalogoCanaisOperacoes.Produtor,
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = comando.CorrelationId, CausationId = comando.MessageId,
        OsId = comando.OsId, FilialId = comando.FilialId,
        ReservationId = reservationId, Items = itens
    };

    public static InventoryReservationRejected ReservaRecusada(MensagemSaga comando, string motivo, IReadOnlyList<Guid> indisponiveis) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = Tipo<InventoryReservationRejected>(), SchemaVersion = 1, Producer = CatalogoCanaisOperacoes.Produtor,
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = comando.CorrelationId, CausationId = comando.MessageId,
        OsId = comando.OsId, FilialId = comando.FilialId,
        Reason = motivo, UnavailablePecaIds = indisponiveis.Count > 0 ? indisponiveis : null
    };

    public static InventoryReleased Liberado(MensagemSaga comando, Guid reservationId, IReadOnlyList<PecaQuantity> itens) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = Tipo<InventoryReleased>(), SchemaVersion = 1, Producer = CatalogoCanaisOperacoes.Produtor,
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = comando.CorrelationId, CausationId = comando.MessageId,
        OsId = comando.OsId, FilialId = comando.FilialId,
        ReservationId = reservationId, ReleasedItems = itens
    };

    // ── Execução ──────────────────────────────────────────────────────────────

    public static DiagnosisCompleted Diagnosticado(OrigemEvento o, Guid executionId, DateTimeOffset snapshotEm, IReadOnlyList<PricedItem> itens) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = Tipo<DiagnosisCompleted>(), SchemaVersion = 1, Producer = CatalogoCanaisOperacoes.Produtor,
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
        ExecutionId = executionId, PriceSnapshotAtUtc = snapshotEm, Currency = Domain.Comum.Dinheiro.Moeda, Items = itens
    };

    public static DiagnosisRejected DiagnosticoRejeitado(OrigemEvento o, string motivo) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = Tipo<DiagnosisRejected>(), SchemaVersion = 1, Producer = CatalogoCanaisOperacoes.Produtor,
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
        Reason = motivo
    };

    public static ExecutionStarted Iniciada(OrigemEvento o, Guid executionId, DateTimeOffset iniciadaEm) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = Tipo<ExecutionStarted>(), SchemaVersion = 1, Producer = CatalogoCanaisOperacoes.Produtor,
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
        ExecutionId = executionId, StartedAtUtc = iniciadaEm
    };

    public static ExecutionStartRejected InicioRecusado(OrigemEvento o, Guid executionId, string motivo) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = Tipo<ExecutionStartRejected>(), SchemaVersion = 1, Producer = CatalogoCanaisOperacoes.Produtor,
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
        ExecutionId = executionId, Reason = motivo
    };

    public static ExecutionCompleted Concluida(OrigemEvento o, Guid executionId, DateTimeOffset concluidaEm, IReadOnlyList<PecaQuantity> consumidas) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = Tipo<ExecutionCompleted>(), SchemaVersion = 1, Producer = CatalogoCanaisOperacoes.Produtor,
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
        ExecutionId = executionId, CompletedAtUtc = concluidaEm, ConsumedItems = consumidas
    };

    public static ExecutionFailed Falhou(OrigemEvento o, Guid executionId, string motivo, IReadOnlyList<PecaQuantity> consumidas) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = Tipo<ExecutionFailed>(), SchemaVersion = 1, Producer = CatalogoCanaisOperacoes.Produtor,
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
        ExecutionId = executionId, Reason = motivo, ConsumedItems = consumidas
    };

    public static string Serializar(MensagemSaga mensagem) => JsonSerializer.Serialize(mensagem, mensagem.GetType(), _opcoes);

    private static string Tipo<T>() => CatalogoCanaisOperacoes.Publicado(typeof(T)).MessageType;
}
