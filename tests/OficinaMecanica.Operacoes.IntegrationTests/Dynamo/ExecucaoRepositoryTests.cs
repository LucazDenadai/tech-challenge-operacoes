using System.Text.Json.Nodes;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using OficinaMecanica.Operacoes.Application.Contratos.Saga;
using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Execucoes;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;
using OficinaMecanica.Operacoes.IntegrationTests.Fixtures;

namespace OficinaMecanica.Operacoes.IntegrationTests.Dynamo;

// Repositório de execução contra o DynamoDB Local: chaves, índice da fila, inbox, outbox e concorrência (CARD-38b).
[Collection(DynamoDbCollection.Nome)]
public class ExecucaoRepositoryTests(DynamoDbFixture fixture) : IAsyncLifetime
{
    private IAmazonDynamoDB _cliente = null!;
    private IOptions<DynamoDbOptions> _opcoes = null!;
    private ExecucaoRepository _repo = null!;

    private readonly Guid _filialId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        (_cliente, _opcoes) = await fixture.NovaTabelaAsync();
        _repo = new ExecucaoRepository(_cliente, _opcoes);
    }

    public Task DisposeAsync()
    {
        _cliente.Dispose();
        return Task.CompletedTask;
    }

    private Execucao Nova(Guid? osId = null)
        => Execucao.Abrir(osId ?? Guid.NewGuid(), _filialId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private static IReadOnlyList<ItemDiagnostico> Itens(Guid peca) =>
        [new(peca, TipoItemDiagnostico.Peca, "Óleo 5W30", 4, 39.90m), new(Guid.NewGuid(), TipoItemDiagnostico.Servico, "Troca de óleo", 1, 80m)];

    private static DiagnosisRequested Comando(Execucao e) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = "DiagnosisRequested.v1", SchemaVersion = 1, Producer = "os",
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = e.CorrelationId, OsId = e.OsId, FilialId = e.FilialId,
        IdempotencyKey = $"diagnostico:{e.OsId}", VeiculoId = e.VeiculoId
    };

    [Fact]
    public async Task GravarELer_PreservaOAgregadoCompleto()
    {
        var peca = Guid.NewGuid();
        var execucao = Nova();
        execucao.RegistrarDiagnostico(Itens(peca), "mecanico@oficina.example");
        execucao.Enfileirar(Guid.NewGuid(), Guid.NewGuid(), "inicio:1");
        execucao.IniciarReparo("mecanico@oficina.example");
        execucao.RegistrarEtapa("Óleo drenado", "mecanico@oficina.example");

        await _repo.GravarAsync(execucao, []);
        var lida = await _repo.ObterAsync(execucao.Id);

        Assert.NotNull(lida);
        Assert.Equal(1, lida.Versao);
        Assert.Equivalent(execucao.Estado(), lida.Estado());
        Assert.Equal(DateTimeKind.Utc, lida.CriadaEm.Kind);
        Assert.Equal(lida.Id, (await _repo.ObterPorOsAsync(execucao.OsId))!.Id);
    }

    [Fact]
    public async Task Fila_PorFilialEStado_AcompanhaAMudancaDeStatus()
    {
        var a = Nova();
        var b = Nova();
        var outraFilial = Execucao.Abrir(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var e in new[] { a, b, outraFilial })
            await _repo.GravarAsync(e, []);

        a.RegistrarDiagnostico(Itens(Guid.NewGuid()), "m");
        await _repo.GravarAsync(a, []);

        var emDiagnostico = await _repo.ListarFilaAsync(_filialId, StatusExecucao.EmDiagnostico);
        var diagnosticadas = await _repo.ListarFilaAsync(_filialId, StatusExecucao.Diagnosticada);

        Assert.Equal([b.Id], emDiagnostico.Select(e => e.Id));
        Assert.Equal([a.Id], diagnosticadas.Select(e => e.Id));
    }

    [Fact]
    public async Task SegundaExecucaoParaAMesmaOs_Conflito()
    {
        var osId = Guid.NewGuid();
        await _repo.GravarAsync(Nova(osId), []);

        await Assert.ThrowsAsync<ConflitoConcorrenciaException>(() => _repo.GravarAsync(Nova(osId), []));
    }

    [Fact]
    public async Task GravacaoComVersaoDesatualizada_Conflito()
    {
        var execucao = Nova();
        await _repo.GravarAsync(execucao, []);
        var copiaA = (await _repo.ObterAsync(execucao.Id))!;
        var copiaB = (await _repo.ObterAsync(execucao.Id))!;

        copiaA.RejeitarDiagnostico("Motivo A", "m");
        await _repo.GravarAsync(copiaA, []);
        copiaB.RegistrarDiagnostico(Itens(Guid.NewGuid()), "m");

        await Assert.ThrowsAsync<ConflitoConcorrenciaException>(() => _repo.GravarAsync(copiaB, []));
        Assert.Equal(StatusExecucao.DiagnosticoRejeitado, (await _repo.ObterAsync(execucao.Id))!.Status);
    }

    [Fact]
    public async Task MensagemRepetida_NaoGravaNadaDeNovo()
    {
        var execucao = Nova();
        var comando = Comando(execucao);
        var evento = RespostaSaga.DiagnosticoRejeitado(OrigemEvento.De(comando), "Filial");
        var recebida = new MensagemRecebida(comando, "saga-os.diagnosis-requested.v1", "{}");

        Assert.True(await _repo.GravarAsync(execucao, [evento], recebida));

        // Segunda entrega: outro agregado (como se o handler o tivesse recriado) e outro evento.
        var repetida = Nova(Guid.NewGuid());
        var outroEvento = RespostaSaga.DiagnosticoRejeitado(OrigemEvento.De(comando), "Filial");
        Assert.False(await _repo.GravarAsync(repetida, [outroEvento], recebida));

        Assert.Null(await _repo.ObterAsync(repetida.Id));
        Assert.Single(await PendentesAsync());
    }

    [Fact]
    public async Task Outbox_GravadaComOAgregadoEPendenteNoIndice()
    {
        var execucao = Nova();
        execucao.RegistrarDiagnostico(Itens(Guid.NewGuid()), "m");
        var evento = RespostaSaga.Diagnosticado(new OrigemEvento(execucao.CorrelationId, execucao.ComandoDiagnosticoId, execucao.OsId, execucao.FilialId),
            execucao.Id, DateTimeOffset.UtcNow,
            [new PricedItem { ItemId = Guid.NewGuid(), Type = "Peca", Description = "Óleo", Quantity = 4, UnitPrice = 39.90m }]);

        await _repo.GravarAsync(execucao, [evento]);

        var pendente = Assert.Single(await PendentesAsync());
        Assert.Equal("saga-os.diagnosis-completed.v1", pendente["Canal"].S);
        Assert.Equal(evento.MessageId.ToString(), pendente["MessageId"].S);
        var payload = JsonNode.Parse(pendente["Payload"].S)!;
        Assert.Equal(execucao.Id.ToString(), payload["executionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task FalhaNaTransacao_NaoDeixaAgregadoSemEvento()
    {
        // O mesmo evento (mesmo messageId) já está na outbox: a condição falha e nada da transação fica.
        var execucao = Nova();
        var evento = RespostaSaga.DiagnosticoRejeitado(OrigemEvento.De(Comando(execucao)), "Filial");
        await _repo.GravarAsync(null, [evento]);

        await Assert.ThrowsAsync<ConflitoConcorrenciaException>(() => _repo.GravarAsync(execucao, [evento]));

        Assert.Null(await _repo.ObterAsync(execucao.Id));
        Assert.Null(await _repo.ObterPorOsAsync(execucao.OsId));
    }

    private async Task<List<Dictionary<string, AttributeValue>>> PendentesAsync()
        => (await _cliente.QueryAsync(new QueryRequest
        {
            TableName = _opcoes.Value.Tabela,
            IndexName = ChavesDynamo.Gsi1,
            KeyConditionExpression = "GSI1PK = :p",
            ExpressionAttributeValues = new() { [":p"] = new AttributeValue { S = ChavesDynamo.OutboxPendente } }
        })).Items ?? [];
}
