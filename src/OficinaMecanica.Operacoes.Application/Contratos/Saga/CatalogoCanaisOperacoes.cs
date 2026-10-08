namespace OficinaMecanica.Operacoes.Application.Contratos.Saga;

public sealed record CanalConsumido(string Endereco, string MessageType, int SchemaVersion, string Produtor, Type TipoMensagem);

public sealed record CanalPublicado(string Endereco, string MessageType, Type TipoMensagem);

// Canais de Operações no AsyncAPI (ADR-018). Testes de contrato comparam estas listas com a spec.
// Diagnóstico e execução (diagnosis-*, execution-*) entram no CARD-38b.
public static class CatalogoCanaisOperacoes
{
    public const string Produtor = "operacoes";

    public static readonly IReadOnlyList<CanalConsumido> Consumidos =
    [
        Consumido<InventoryReservationRequested>("saga-os.inventory-reservation-requested.v1"),
        Consumido<InventoryReleaseRequested>("saga-os.inventory-release-requested.v1")
    ];

    public static readonly IReadOnlyList<CanalPublicado> Publicados =
    [
        Publicado<InventoryReserved>("saga-os.inventory-reserved.v1"),
        Publicado<InventoryReservationRejected>("saga-os.inventory-reservation-rejected.v1"),
        Publicado<InventoryReleased>("saga-os.inventory-released.v1")
    ];

    public static CanalConsumido? ObterConsumido(string endereco) => Consumidos.FirstOrDefault(c => c.Endereco == endereco);

    public static CanalPublicado Publicado(Type tipo)
        => Publicados.FirstOrDefault(c => c.TipoMensagem == tipo)
           ?? throw new InvalidOperationException($"Operações não publica {tipo.Name}.");

    // Todos os canais são v1: o messageType é o nome do DTO + ".v1", como `name` das mensagens na spec.
    private static CanalConsumido Consumido<T>(string endereco) where T : MensagemSaga
        => new(endereco, $"{typeof(T).Name}.v1", 1, "os", typeof(T));

    private static CanalPublicado Publicado<T>(string endereco) where T : MensagemSaga
        => new(endereco, $"{typeof(T).Name}.v1", typeof(T));
}
