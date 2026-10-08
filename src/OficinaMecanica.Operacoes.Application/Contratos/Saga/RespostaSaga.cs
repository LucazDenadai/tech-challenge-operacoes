using System.Text.Json;
using System.Text.Json.Serialization;

namespace OficinaMecanica.Operacoes.Application.Contratos.Saga;

// Monta e serializa os eventos que Operações publica em resposta a um comando da Saga.
public static class RespostaSaga
{
    private static readonly JsonSerializerOptions _opcoes = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Envelope da resposta: mesma correlação, OS e filial do comando; causationId = messageId do comando (ADR-017).
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

    public static string Serializar(MensagemSaga mensagem) => JsonSerializer.Serialize(mensagem, mensagem.GetType(), _opcoes);

    private static string Tipo<T>() => CatalogoCanaisOperacoes.Publicado(typeof(T)).MessageType;
}
