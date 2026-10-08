using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Seed;
using OficinaMecanica.Operacoes.IntegrationTests.Fixtures;

namespace OficinaMecanica.Operacoes.IntegrationTests.Persistence;

[Collection(PostgresCollection.Nome)]
public class MigrationsESeedTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_CriamSomenteCatalogoEEstoque()
    {
        // ADR-016: o PostgreSQL de Operações guarda só catálogo e estoque. Nada de OS, Billing ou execução.
        await using var db = PostgresFixture.CriarContexto(await fixture.NovoBancoMigradoAsync());

        var tabelas = await db.Database
            .SqlQueryRaw<string>("""
                SELECT table_schema || '.' || table_name AS "Value"
                FROM information_schema.tables
                WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
                """)
            .ToListAsync();

        Assert.Equal(
            new[]
            {
                "public.Filiais", "public.InboxMensagens", "public.ItensReserva", "public.Movimentacoes", "public.OutboxMensagens",
                "public.Pecas", "public.Reservas", "public.Saldos", "public.Servicos", "public.__EFMigrationsHistory"
            }.Order(),
            tabelas.Order());
    }

    [Fact]
    public async Task Migrations_NaoTemMudancasPendentesNoModelo()
    {
        await using var db = PostgresFixture.CriarContexto(await fixture.NovoBancoMigradoAsync());

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Seed_CriaFilialComIdDoOsCatalogoESaldos_EEhIdempotente()
    {
        var connectionString = await fixture.NovoBancoMigradoAsync();
        await using (var db = PostgresFixture.CriarContexto(connectionString))
            await SeedDemonstracao.ExecutarAsync(db);
        await using (var db = PostgresFixture.CriarContexto(connectionString))
            await SeedDemonstracao.ExecutarAsync(db);

        await using var leitura = PostgresFixture.CriarContexto(connectionString);
        var filial = Assert.Single(await leitura.Filiais.ToListAsync());
        Assert.Equal(Guid.Parse("378aeb39-37f6-43c1-9526-5b1a9fadd553"), filial.Id);
        Assert.Equal(5, await leitura.Pecas.CountAsync());
        Assert.Equal(4, await leitura.Servicos.CountAsync());
        Assert.Equal(5, await leitura.Saldos.CountAsync(s => s.FilialId == filial.Id));

        // Uma entrada por peça com saldo inicial; a segunda execução não duplica.
        Assert.Equal(4, await leitura.Movimentacoes.CountAsync());
        var vela = await leitura.Pecas.SingleAsync(p => p.Codigo == "VELA-IGN-01");
        Assert.Equal(0, (await leitura.Saldos.SingleAsync(s => s.PecaId == vela.Id)).QuantidadeDisponivel);
    }
}
