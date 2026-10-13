using OficinaMecanica.Operacoes.Application.Contratos.Saga;

namespace OficinaMecanica.Operacoes.UnitTests.Contratos;

// Operações não lê os eventos que publica, então os validadores deles só rodam aqui: cada evento montado pela
// RespostaSaga passa no próprio validador, e um evento malformado é recusado com o campo certo (ADR-018).
public class EventosPublicadosValidacaoTests
{
    private static readonly OrigemEvento _origem = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly PecaQuantity[] _pecas = [new() { PecaId = Guid.NewGuid(), Quantity = 2 }];

    private static PricedItem Item(string tipo = "Peca", string descricao = "Filtro", int quantidade = 1, decimal preco = 45.90m)
        => new() { ItemId = Guid.NewGuid(), Type = tipo, Description = descricao, Quantity = quantidade, UnitPrice = preco };

    public static TheoryData<string> Validos() => new() { "diagnosticado", "concluida", "concluida-sem-consumo", "falhou", "cancelada", "cancelamento-recusado" };

    [Theory]
    [MemberData(nameof(Validos))]
    public void EventoMontadoPelaResposta_PassaNoProprioValidador(string caso)
    {
        MensagemSaga evento = caso switch
        {
            "diagnosticado" => RespostaSaga.Diagnosticado(_origem, Guid.NewGuid(), DateTimeOffset.UtcNow, [Item(), Item("Servico", "Troca", 1, 80m)]),
            "concluida" => RespostaSaga.Concluida(_origem, Guid.NewGuid(), DateTimeOffset.UtcNow, _pecas),
            "concluida-sem-consumo" => RespostaSaga.Concluida(_origem, Guid.NewGuid(), DateTimeOffset.UtcNow, []),
            "falhou" => RespostaSaga.Falhou(_origem, Guid.NewGuid(), "Peça danificada", _pecas),
            "cancelada" => RespostaSaga.Cancelada(_origem, Guid.NewGuid()),
            "cancelamento-recusado" => RespostaSaga.CancelamentoRecusado(_origem, Guid.NewGuid(), "Em reparo"),
            _ => throw new ArgumentOutOfRangeException(nameof(caso))
        };

        Assert.Empty(evento.Validar());
    }

    public static TheoryData<string, string> Invalidos() => new()
    {
        { "diagnostico-sem-itens", "items deve ter ao menos um item" },
        { "diagnostico-moeda-minuscula", "currency" },
        { "item-tipo-fora-do-enum", "items[0].type" },
        { "item-sem-descricao", "items[0].description" },
        { "item-quantidade-zero", "items[0].quantity" },
        { "item-preco-com-tres-casas", "items[0].unitPrice" },
        { "item-nulo", "items[0] não pode ser nulo" },
        { "falha-sem-motivo", "reason" },
        { "falha-consumo-negativo", "consumedItems[0].quantity" }
    };

    [Theory]
    [MemberData(nameof(Invalidos))]
    public void EventoMalformado_RecusadoComOCampo(string caso, string campo)
    {
        MensagemSaga evento = caso switch
        {
            "diagnostico-sem-itens" => RespostaSaga.Diagnosticado(_origem, Guid.NewGuid(), DateTimeOffset.UtcNow, []),
            "diagnostico-moeda-minuscula" => RespostaSaga.Diagnosticado(_origem, Guid.NewGuid(), DateTimeOffset.UtcNow, [Item()]) with { Currency = "brl" },
            "item-tipo-fora-do-enum" => Diagnostico(Item("Outro")),
            "item-sem-descricao" => Diagnostico(Item(descricao: "")),
            "item-quantidade-zero" => Diagnostico(Item(quantidade: 0)),
            "item-preco-com-tres-casas" => Diagnostico(Item(preco: 10.001m)),
            "item-nulo" => Diagnostico(null!),
            "falha-sem-motivo" => RespostaSaga.Falhou(_origem, Guid.NewGuid(), "", _pecas),
            "falha-consumo-negativo" => RespostaSaga.Falhou(_origem, Guid.NewGuid(), "Falha", [new() { PecaId = Guid.NewGuid(), Quantity = -1 }]),
            _ => throw new ArgumentOutOfRangeException(nameof(caso))
        };

        Assert.Contains(evento.Validar(), e => e.Contains(campo));
    }

    private static DiagnosisCompleted Diagnostico(PricedItem item)
        => RespostaSaga.Diagnosticado(_origem, Guid.NewGuid(), DateTimeOffset.UtcNow, [item]);
}
