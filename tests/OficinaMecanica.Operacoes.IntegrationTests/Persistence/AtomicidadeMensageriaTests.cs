using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OficinaMecanica.Operacoes.Application.Contratos.Saga;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Application.UseCases.Estoque;
using OficinaMecanica.Operacoes.Application.UseCases.Execucoes;
using OficinaMecanica.Operacoes.Application.UseCases.Mensageria;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Repositories;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Seed;
using OficinaMecanica.Operacoes.IntegrationTests.Fixtures;

namespace OficinaMecanica.Operacoes.IntegrationTests.Persistence;

// ADR-016: inbox, efeito e outbox são confirmados juntos. Uma falha no meio não deixa nenhum dos três.
[Collection(PostgresCollection.Nome)]
public class AtomicidadeMensageriaTests(PostgresFixture fixture, DynamoDbFixture dynamo)
{
    [Fact]
    public async Task FalhaAoGravarOutbox_DesfazInboxEReserva()
    {
        var conexao = await fixture.NovoBancoMigradoAsync();
        Guid oleo;
        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            await SeedDemonstracao.ExecutarAsync(db);
            oleo = (await db.Pecas.SingleAsync(p => p.Codigo == "OLEO-5W30-1L")).Id;
        }
        var corpo = Pedido(oleo);

        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Processar(db, new OutboxQueFalha()).ExecutarAsync(Canal, corpo));
        }

        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            Assert.Equal(0, await db.Inbox.CountAsync());
            Assert.Equal(0, await db.Reservas.CountAsync());
            Assert.Equal(0, (await db.Saldos.SingleAsync(s => s.PecaId == oleo)).QuantidadeReservada);
        }

        // A nova tentativa da mesma mensagem é processada normalmente: nada ficou marcado como recebido.
        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            var resultado = await Processar(db, new OutboxRepository(db)).ExecutarAsync(Canal, corpo);
            Assert.Equal(StatusProcessamento.Processada, resultado.Status);
        }

        await using var leitura = PostgresFixture.CriarContexto(conexao);
        Assert.Equal(1, await leitura.Inbox.CountAsync());
        Assert.Equal(1, await leitura.Reservas.CountAsync());
        Assert.Equal("InventoryReserved.v1", (await leitura.Outbox.SingleAsync()).MessageType);
    }

    private const string Canal = "saga-os.inventory-reservation-requested.v1";

    private ProcessarMensagemSagaUseCase Processar(AppDbContext db, IOutbox outbox)
        => new(new Transacao(db), new InboxRepository(db), outbox, new UnidadeDeTrabalho(db),
            new ReservarEstoqueUseCase(new FilialEstoqueRepository(db), new SaldoEstoqueRepository(db), new ReservaRepository(db), new MovimentacaoRepository(db)),
            new LiberarReservaUseCase(new ReservaRepository(db), new SaldoEstoqueRepository(db), new MovimentacaoRepository(db)),
            new ComandosExecucaoUseCase(new ExecucaoRepository(dynamo.Opcoes().CriarCliente(), Microsoft.Extensions.Options.Options.Create(dynamo.Opcoes())),
                new ConsumirReservaUseCase(new FilialEstoqueRepository(db), new ReservaRepository(db), new SaldoEstoqueRepository(db), new MovimentacaoRepository(db), new UnidadeDeTrabalho(db))),
            NullLogger<ProcessarMensagemSagaUseCase>.Instance);

    private static ReadOnlyMemory<byte> Pedido(Guid pecaId)
    {
        var osId = Guid.NewGuid();
        var json = new JsonObject
        {
            ["messageId"] = Guid.NewGuid().ToString(),
            ["messageType"] = "InventoryReservationRequested.v1",
            ["schemaVersion"] = 1,
            ["producer"] = "os",
            ["occurredAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["correlationId"] = Guid.NewGuid().ToString(),
            ["osId"] = osId.ToString(),
            ["filialId"] = SeedDemonstracao.FilialDemoId.ToString(),
            ["idempotencyKey"] = $"reserva:{osId}",
            ["items"] = new JsonArray(new JsonObject { ["pecaId"] = pecaId.ToString(), ["quantity"] = 2 })
        };
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    private sealed class OutboxQueFalha : IOutbox
    {
        public void Adicionar(MensagemSaga mensagem) => throw new InvalidOperationException("Falha simulada na outbox.");
    }
}
