using System.Text.Json.Nodes;
using OficinaMecanica.Operacoes.Application.Contratos.Saga;

namespace OficinaMecanica.Operacoes.UnitTests.Contratos;

// Testes de contrato do ADR-018: os DTOs, o validador e os eventos publicados por Operações concordam
// com o JSON Schema da spec AsyncAPI.
public class ContratoAsyncApiTests
{
    public static TheoryData<string> Canais()
    {
        var dados = new TheoryData<string>();
        foreach (var canal in CatalogoCanaisOperacoes.Consumidos)
            dados.Add(canal.Endereco);
        return dados;
    }

    // Mutações aplicadas a cada exemplo quando o campo existe; spec e validador devem rejeitar todas.
    private static readonly (string Descricao, Func<JsonObject, bool> Aplicar)[] _mutacoesInvalidas =
    [
        ("messageId não é UUID", m => Trocar(m, "messageId", "nao-e-uuid")),
        ("occurredAtUtc não é data", m => Trocar(m, "occurredAtUtc", "ontem")),
        ("schemaVersion zero", m => Trocar(m, "schemaVersion", 0)),
        ("producer desconhecido", m => Trocar(m, "producer", "estoque")),
        ("osId numérico", m => Trocar(m, "osId", 123)),
        ("idempotencyKey vazia", m => Trocar(m, "idempotencyKey", "")),
        ("reason vazio", m => Trocar(m, "reason", "")),
        ("reservationId não é UUID", m => Trocar(m, "reservationId", "r-1")),
        ("items vazio", m => Trocar(m, "items", new JsonArray())),
        ("item com quantidade negativa", m => TrocarNoItem(m, "items", "quantity", -1)),
        ("item com pecaId inválido", m => TrocarNoItem(m, "items", "pecaId", "peca")),
        ("item nulo", m => m["items"] is JsonArray itens && Substituir(itens))
    ];

    [Fact]
    public void Consumidos_CorrespondemAosCanaisDaSpecCujoConsumidorEOperacoes()
    {
        var spec = SpecAsyncApi.CanaisConsumidosPorOperacoes();

        Assert.Equal(spec.Keys.Order(), CatalogoCanaisOperacoes.Consumidos.Select(c => c.Endereco).Order());
        foreach (var canal in CatalogoCanaisOperacoes.Consumidos)
            Assert.Equal(SpecAsyncApi.NomeVersionado(spec[canal.Endereco]), canal.MessageType);
    }

    [Fact]
    public void Publicados_CorrespondemAosCanaisDaSpecCujoProdutorEOperacoes()
    {
        var spec = SpecAsyncApi.CanaisProduzidosPorOperacoes();

        Assert.Equal(spec.Keys.Order(), CatalogoCanaisOperacoes.Publicados.Select(c => c.Endereco).Order());
        foreach (var canal in CatalogoCanaisOperacoes.Publicados)
            Assert.Equal(SpecAsyncApi.NomeVersionado(spec[canal.Endereco]), canal.MessageType);
    }

    [Theory]
    [MemberData(nameof(Canais))]
    public void ExemploValido_AceitoPelaSpecEPeloValidador(string endereco)
    {
        var canal = CatalogoCanaisOperacoes.ObterConsumido(endereco)!;
        var exemplo = MensagensExemplo.Criar(canal);

        Assert.True(SpecAsyncApi.Valida(NomeNaSpec(canal), exemplo), "A spec rejeitou o exemplo; ajuste MensagensExemplo.");
        var leitura = LeitorMensagemSaga.Ler(canal, MensagensExemplo.Bytes(exemplo));
        Assert.True(leitura.Valida, leitura.Motivo);
    }

    [Theory]
    [MemberData(nameof(Canais))]
    public void CampoDesconhecido_AceitoPelaSpecEPeloValidador(string endereco)
    {
        // Campo opcional novo é mudança compatível (ADR-018): o consumidor não pode rejeitar.
        var canal = CatalogoCanaisOperacoes.ObterConsumido(endereco)!;
        var exemplo = MensagensExemplo.Criar(canal);
        exemplo["campoNovoOpcional"] = "valor";

        Assert.True(SpecAsyncApi.Valida(NomeNaSpec(canal), exemplo));
        Assert.True(LeitorMensagemSaga.Ler(canal, MensagensExemplo.Bytes(exemplo)).Valida);
    }

    [Theory]
    [MemberData(nameof(Canais))]
    public void CadaCampoObrigatorioDaSpec_AusenteERejeitadoPeloValidador(string endereco)
    {
        var canal = CatalogoCanaisOperacoes.ObterConsumido(endereco)!;
        var aceitos = new List<string>();

        foreach (var campo in SpecAsyncApi.CamposObrigatorios(NomeNaSpec(canal)))
        {
            var exemplo = MensagensExemplo.Criar(canal);
            exemplo.Remove(campo);

            Assert.False(SpecAsyncApi.Valida(NomeNaSpec(canal), exemplo), $"Spec aceitou mensagem sem '{campo}'.");
            if (LeitorMensagemSaga.Ler(canal, MensagensExemplo.Bytes(exemplo)).Valida)
                aceitos.Add(campo);
        }

        Assert.True(aceitos.Count == 0, $"Validador aceitou mensagem sem: {string.Join(", ", aceitos)}");
    }

    [Theory]
    [MemberData(nameof(Canais))]
    public void MutacaoInvalida_RejeitadaPelaSpecEPeloValidador(string endereco)
    {
        var canal = CatalogoCanaisOperacoes.ObterConsumido(endereco)!;
        var divergencias = new List<string>();

        foreach (var (descricao, aplicar) in _mutacoesInvalidas)
        {
            var exemplo = MensagensExemplo.Criar(canal);
            if (!aplicar(exemplo)) continue;

            var spec = SpecAsyncApi.Valida(NomeNaSpec(canal), exemplo);
            var validador = LeitorMensagemSaga.Ler(canal, MensagensExemplo.Bytes(exemplo)).Valida;
            if (spec || validador)
                divergencias.Add($"{descricao} (spec aceitou: {spec}, validador aceitou: {validador})");
        }

        Assert.True(divergencias.Count == 0, string.Join("; ", divergencias));
    }

    [Fact]
    public void MensagemDeOutroProdutor_RejeitadaPeloValidador()
    {
        // O producer é válido na spec, mas não é o produtor do canal: só o OS comanda Operações.
        var canal = CatalogoCanaisOperacoes.Consumidos[0];
        var exemplo = MensagensExemplo.Criar(canal);
        exemplo["producer"] = "billing";

        Assert.False(LeitorMensagemSaga.Ler(canal, MensagensExemplo.Bytes(exemplo)).Valida);
    }

    public static TheoryData<string> EventosPublicados() => new()
    {
        "reservado", "recusado-com-pecas", "recusado-sem-pecas", "liberado", "liberado-sem-itens",
        "diagnosticado", "diagnostico-rejeitado", "iniciada", "inicio-recusado", "concluida", "concluida-sem-consumo", "falhou"
    };

    [Theory]
    [MemberData(nameof(EventosPublicados))]
    public void EventoPublicado_SerializadoConformeASpec(string caso)
    {
        var comando = (MensagemSaga)LeitorMensagemSaga.Ler(CatalogoCanaisOperacoes.Consumidos[0],
            MensagensExemplo.Bytes(MensagensExemplo.Criar(CatalogoCanaisOperacoes.Consumidos[0]))).Mensagem!;
        var pecas = new[] { new PecaQuantity { PecaId = Guid.NewGuid(), Quantity = 2 } };

        MensagemSaga evento = caso switch
        {
            "reservado" => RespostaSaga.Reservado(comando, Guid.NewGuid(), pecas),
            "recusado-com-pecas" => RespostaSaga.ReservaRecusada(comando, "Saldo insuficiente na filial.", [Guid.NewGuid()]),
            "recusado-sem-pecas" => RespostaSaga.ReservaRecusada(comando, "A filial não opera estoque em Operações.", []),
            "liberado" => RespostaSaga.Liberado(comando, Guid.NewGuid(), pecas),
            "liberado-sem-itens" => RespostaSaga.Liberado(comando, Guid.NewGuid(), []),
            "diagnosticado" => RespostaSaga.Diagnosticado(OrigemEvento.De(comando), Guid.NewGuid(), DateTimeOffset.UtcNow,
                [new PricedItem { ItemId = Guid.NewGuid(), Type = "Peca", Description = "Filtro de óleo", Quantity = 1, UnitPrice = 45.90m },
                 new PricedItem { ItemId = Guid.NewGuid(), Type = "Servico", Description = "Troca de óleo", Quantity = 1, UnitPrice = 80m }]),
            "diagnostico-rejeitado" => RespostaSaga.DiagnosticoRejeitado(OrigemEvento.De(comando), "A filial não opera em Operações."),
            "iniciada" => RespostaSaga.Iniciada(OrigemEvento.De(comando), Guid.NewGuid(), DateTimeOffset.UtcNow),
            "inicio-recusado" => RespostaSaga.InicioRecusado(OrigemEvento.De(comando), Guid.NewGuid(), "Reserva inexistente."),
            "concluida" => RespostaSaga.Concluida(OrigemEvento.De(comando), Guid.NewGuid(), DateTimeOffset.UtcNow, pecas),
            "concluida-sem-consumo" => RespostaSaga.Concluida(OrigemEvento.De(comando), Guid.NewGuid(), DateTimeOffset.UtcNow, []),
            "falhou" => RespostaSaga.Falhou(OrigemEvento.De(comando), Guid.NewGuid(), "Peça danificada na instalação", pecas),
            _ => throw new ArgumentOutOfRangeException(nameof(caso))
        };

        var json = JsonNode.Parse(RespostaSaga.Serializar(evento))!;
        var canal = CatalogoCanaisOperacoes.Publicado(evento.GetType());
        var nome = SpecAsyncApi.CanaisProduzidosPorOperacoes()[canal.Endereco];

        Assert.True(SpecAsyncApi.Valida(nome, json), $"A spec rejeitou {evento.MessageType}: {json.ToJsonString()}");
        Assert.Equal("operacoes", json["producer"]!.GetValue<string>());
        Assert.Equal(comando.MessageId.ToString(), json["causationId"]!.GetValue<string>());
        Assert.Equal(comando.CorrelationId.ToString(), json["correlationId"]!.GetValue<string>());
        Assert.Null(json["idempotencyKey"]);
    }

    private static string NomeNaSpec(CanalConsumido canal) => SpecAsyncApi.CanaisConsumidosPorOperacoes()[canal.Endereco];

    private static bool Trocar(JsonObject mensagem, string campo, JsonNode? valor)
    {
        if (!mensagem.ContainsKey(campo)) return false;
        mensagem[campo] = valor;
        return true;
    }

    private static bool TrocarNoItem(JsonObject mensagem, string lista, string campo, JsonNode valor)
    {
        if (mensagem[lista] is not JsonArray { Count: > 0 } itens || itens[0] is not JsonObject item || !item.ContainsKey(campo))
            return false;
        item[campo] = valor;
        return true;
    }

    private static bool Substituir(JsonArray itens)
    {
        itens.Clear();
        itens.Add(null);
        return true;
    }
}
