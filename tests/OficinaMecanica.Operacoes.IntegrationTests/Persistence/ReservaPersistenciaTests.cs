using Microsoft.EntityFrameworkCore;
using Npgsql;
using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.UseCases.Estoque;
using OficinaMecanica.Operacoes.Domain.Catalogo;
using OficinaMecanica.Operacoes.Domain.Estoque;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Repositories;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Seed;
using OficinaMecanica.Operacoes.IntegrationTests.Fixtures;

namespace OficinaMecanica.Operacoes.IntegrationTests.Persistence;

[Collection(PostgresCollection.Nome)]
public class ReservaPersistenciaTests(PostgresFixture fixture)
{
    private static readonly Guid Filial = SeedDemonstracao.FilialDemoId;

    private async Task<(string Conexao, Peca Oleo, Peca Pastilha)> PrepararAsync()
    {
        var conexao = await fixture.NovoBancoMigradoAsync();
        await using var db = PostgresFixture.CriarContexto(conexao);
        await SeedDemonstracao.ExecutarAsync(db);
        return (conexao,
            await db.Pecas.SingleAsync(p => p.Codigo == "OLEO-5W30-1L"),
            await db.Pecas.SingleAsync(p => p.Codigo == "PST-FREIO-D"));
    }

    private sealed class Servicos(AppDbContext db)
    {
        public UnidadeDeTrabalho Uow { get; } = new(db);
        public ReservarEstoqueUseCase Reservar { get; } = new(new FilialEstoqueRepository(db), new SaldoEstoqueRepository(db), new ReservaRepository(db), new MovimentacaoRepository(db));
        public LiberarReservaUseCase Liberar { get; } = new(new ReservaRepository(db), new SaldoEstoqueRepository(db), new MovimentacaoRepository(db));
        public ConsumirReservaUseCase Consumir { get; } = new(new ReservaRepository(db), new SaldoEstoqueRepository(db), new MovimentacaoRepository(db), new UnidadeDeTrabalho(db));
    }

    private static ReservarEstoqueCommand Comando(Guid osId, params ItemQuantidade[] itens)
        => new(osId, Filial, Guid.NewGuid(), $"reserva:{osId}", itens);

    [Fact]
    public async Task CicloReservaFalhaLiberacao_PersisteSaldosEMovimentacoesCorrelacionadas()
    {
        var (conexao, oleo, pastilha) = await PrepararAsync();
        var osId = Guid.NewGuid();
        var comando = Comando(osId, new ItemQuantidade(oleo.Id, 4), new ItemQuantidade(pastilha.Id, 1));

        Guid reservaId;
        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            var s = new Servicos(db);
            reservaId = Assert.IsType<ReservaConfirmada>(await s.Reservar.ExecutarAsync(comando)).ReservaId;
            await s.Uow.SalvarAsync();
        }

        await using (var db = PostgresFixture.CriarContexto(conexao))
            await new Servicos(db).Consumir.RegistrarConsumoComFalhaAsync(reservaId, [new(oleo.Id, 1)]);

        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            var s = new Servicos(db);
            var liberada = await s.Liberar.ExecutarAsync(reservaId, "Execução falhou");
            await s.Uow.SalvarAsync();
            Assert.Equal(2, liberada.ItensLiberados.Count);
        }

        await using var leitura = PostgresFixture.CriarContexto(conexao);
        var saldoOleo = await leitura.Saldos.SingleAsync(s => s.FilialId == Filial && s.PecaId == oleo.Id);
        Assert.Equal(59, saldoOleo.QuantidadeDisponivel);
        Assert.Equal(0, saldoOleo.QuantidadeReservada);

        var movs = await leitura.Movimentacoes.Where(m => m.OsId == osId).ToListAsync();
        Assert.Equal(5, movs.Count); // 2 reservas, 1 consumo, 2 liberações
        Assert.All(movs, m =>
        {
            Assert.Equal(reservaId, m.ReservaId);
            Assert.Equal(comando.CorrelationId, m.CorrelationId);
        });

        var reserva = await leitura.Reservas.Include(r => r.Itens).SingleAsync(r => r.Id == reservaId);
        Assert.Equal(StatusReserva.Liberada, reserva.Status);
    }

    [Fact]
    public async Task MesmaChaveEmOutroContexto_DevolveReservaExistente()
    {
        var (conexao, oleo, _) = await PrepararAsync();
        var comando = Comando(Guid.NewGuid(), new ItemQuantidade(oleo.Id, 2));

        Guid primeira, segunda;
        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            var s = new Servicos(db);
            primeira = ((ReservaConfirmada)await s.Reservar.ExecutarAsync(comando)).ReservaId;
            await s.Uow.SalvarAsync();
        }
        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            var s = new Servicos(db);
            segunda = ((ReservaConfirmada)await s.Reservar.ExecutarAsync(comando)).ReservaId;
            await s.Uow.SalvarAsync();
        }

        Assert.Equal(primeira, segunda);
        await using var leitura = PostgresFixture.CriarContexto(conexao);
        Assert.Equal(2, (await leitura.Saldos.SingleAsync(s => s.PecaId == oleo.Id)).QuantidadeReservada);
    }

    [Fact]
    public async Task ReservasSimultaneasDoMesmoSaldo_SegundaRecebeConflitoEPodeRefazer()
    {
        // A pastilha tem 8 no seed. Duas reservas leem o mesmo saldo antes de qualquer uma gravar.
        var (conexao, _, pastilha) = await PrepararAsync();

        await using var db1 = PostgresFixture.CriarContexto(conexao);
        await using var db2 = PostgresFixture.CriarContexto(conexao);
        var s1 = new Servicos(db1);
        var s2 = new Servicos(db2);

        Assert.IsType<ReservaConfirmada>(await s1.Reservar.ExecutarAsync(Comando(Guid.NewGuid(), new ItemQuantidade(pastilha.Id, 6))));
        Assert.IsType<ReservaConfirmada>(await s2.Reservar.ExecutarAsync(Comando(Guid.NewGuid(), new ItemQuantidade(pastilha.Id, 6))));

        await s1.Uow.SalvarAsync();
        await Assert.ThrowsAsync<ConflitoConcorrenciaException>(() => s2.Uow.SalvarAsync());

        // Ao refazer com dados novos, a segunda reserva é recusada: o saldo nunca passa do disponível.
        await using var db3 = PostgresFixture.CriarContexto(conexao);
        var refeita = await new Servicos(db3).Reservar.ExecutarAsync(Comando(Guid.NewGuid(), new ItemQuantidade(pastilha.Id, 6)));
        Assert.IsType<ReservaRecusada>(refeita);

        await using var leitura = PostgresFixture.CriarContexto(conexao);
        var saldo = await leitura.Saldos.SingleAsync(s => s.PecaId == pastilha.Id);
        Assert.Equal(2, saldo.QuantidadeDisponivel);
        Assert.Equal(6, saldo.QuantidadeReservada);
    }

    [Fact]
    public async Task ChaveDeIdempotenciaRepetidaEmGravacoesSimultaneas_BancoRecusa()
    {
        // Proteção final além da consulta prévia: o índice único não deixa duas reservas com a mesma chave.
        var (conexao, oleo, pastilha) = await PrepararAsync();
        var osId = Guid.NewGuid();

        await using var db1 = PostgresFixture.CriarContexto(conexao);
        await using var db2 = PostgresFixture.CriarContexto(conexao);
        var s1 = new Servicos(db1);
        var s2 = new Servicos(db2);
        await s1.Reservar.ExecutarAsync(Comando(osId, new ItemQuantidade(oleo.Id, 1)));
        await s2.Reservar.ExecutarAsync(Comando(osId, new ItemQuantidade(pastilha.Id, 1)));

        await s1.Uow.SalvarAsync();
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s2.Uow.SalvarAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [Fact]
    public async Task ConclusaoPersisteConsumoESobraLiberada()
    {
        var (conexao, oleo, _) = await PrepararAsync();

        Guid reservaId;
        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            var s = new Servicos(db);
            reservaId = ((ReservaConfirmada)await s.Reservar.ExecutarAsync(Comando(Guid.NewGuid(), new ItemQuantidade(oleo.Id, 5)))).ReservaId;
            await s.Uow.SalvarAsync();
        }
        await using (var db = PostgresFixture.CriarContexto(conexao))
            await new Servicos(db).Consumir.ConcluirConsumoAsync(reservaId, [new(oleo.Id, 4)]);
        await using (var db = PostgresFixture.CriarContexto(conexao))
            await new Servicos(db).Consumir.ConcluirConsumoAsync(reservaId, [new(oleo.Id, 4)]);

        await using var leitura = PostgresFixture.CriarContexto(conexao);
        var saldo = await leitura.Saldos.SingleAsync(s => s.PecaId == oleo.Id);
        Assert.Equal(56, saldo.QuantidadeDisponivel);
        Assert.Equal(0, saldo.QuantidadeReservada);
        var item = Assert.Single((await leitura.Reservas.Include(r => r.Itens).SingleAsync(r => r.Id == reservaId)).Itens);
        Assert.Equal(4, item.QuantidadeConsumida);
        Assert.Equal(1, item.QuantidadeLiberada);
    }
}
