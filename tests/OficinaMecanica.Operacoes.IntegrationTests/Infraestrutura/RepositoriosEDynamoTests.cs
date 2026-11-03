using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Catalogo;
using OficinaMecanica.Operacoes.Domain.Estoque;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Repositories;
using OficinaMecanica.Operacoes.IntegrationTests.Fixtures;

namespace OficinaMecanica.Operacoes.IntegrationTests.Infraestrutura;

[Collection(PostgresCollection.Nome)]
public class RepositoriosEDynamoTests(PostgresFixture postgres, DynamoDbFixture dynamo)
{
    [Fact]
    public async Task FilialESaldoNovos_GravadosPelosRepositorios()
    {
        var banco = await postgres.NovoBancoMigradoAsync();
        var filial = new FilialEstoque(Guid.NewGuid(), "FILIAL-NOVA");
        var peca = new Peca("FLT-01", "Filtro", "", 45.90m);
        await using (var db = PostgresFixture.CriarContexto(banco))
        {
            db.Pecas.Add(peca);
            await new FilialEstoqueRepository(db).AdicionarAsync(filial);
            await db.SaveChangesAsync();
            var saldo = new SaldoEstoque(filial.Id, peca.Id);
            saldo.RegistrarEntrada(3, "Compra");
            await new SaldoEstoqueRepository(db).AdicionarAsync(saldo);
            await db.SaveChangesAsync();
        }

        await using var leitura = PostgresFixture.CriarContexto(banco);
        Assert.Equal("FILIAL-NOVA", (await new FilialEstoqueRepository(leitura).ObterPorIdAsync(filial.Id))!.Codigo);
        Assert.Equal(3, Assert.Single(await new SaldoEstoqueRepository(leitura).ListarPorFilialAsync(filial.Id)).QuantidadeDisponivel);
    }

    [Fact]
    public async Task Movimentacoes_FiltradasPelaCorrelacao()
    {
        var banco = await postgres.NovoBancoMigradoAsync();
        var correlacao = Guid.NewGuid();
        var filialId = Guid.NewGuid();
        await using (var db = PostgresFixture.CriarContexto(banco))
        {
            var peca = new Peca("FLT-01", "Filtro", "", 45.90m);
            var saldo = new SaldoEstoque(filialId, peca.Id);
            saldo.RegistrarEntrada(5, "Compra");
            db.Pecas.Add(peca);
            await new FilialEstoqueRepository(db).AdicionarAsync(new FilialEstoque(filialId, "FILIAL-MOV"));
            await new SaldoEstoqueRepository(db).AdicionarAsync(saldo);
            var saldos = new Dictionary<Guid, SaldoEstoque> { [saldo.PecaId] = saldo };
            var (reserva, reservada) = Reserva.Reservar(Guid.NewGuid(), filialId, correlacao, "chave-1", [new ItemQuantidade(saldo.PecaId, 1)], saldos);
            var (reservaOutra, outra) = Reserva.Reservar(Guid.NewGuid(), filialId, Guid.NewGuid(), "chave-2", [new ItemQuantidade(saldo.PecaId, 1)], saldos);
            db.Reservas.AddRange(reserva, reservaOutra);
            await new MovimentacaoRepository(db).AdicionarAsync(reservada.Concat(outra));
            await db.SaveChangesAsync();
        }

        await using var leitura = PostgresFixture.CriarContexto(banco);
        var filtradas = await new MovimentacaoRepository(leitura).ListarAsync(new FiltroMovimentacao(filialId, CorrelationId: correlacao));

        var movimentacao = Assert.Single(filtradas);
        Assert.Equal(correlacao, movimentacao.CorrelationId);
    }

    [Fact]
    public async Task TabelaDynamo_CriarDuasVezes_NaoFalha()
    {
        var (cliente, opcoes) = await dynamo.NovaTabelaAsync();

        await InicializadorTabelaDynamo.CriarSeNaoExisteAsync(cliente, opcoes.Value.Tabela);

        Assert.Equal("ACTIVE", (await cliente.DescribeTableAsync(opcoes.Value.Tabela)).Table.TableStatus.Value);
    }

    [Fact]
    public async Task ReadinessDoDynamo_TabelaInexistente_Indisponivel()
    {
        var (cliente, _) = await dynamo.NovaTabelaAsync();
        var check = new DynamoDbHealthCheck(cliente, Options.Create(dynamo.Opcoes($"inexistente-{Guid.NewGuid():N}")));

        var resultado = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, resultado.Status);
        Assert.NotNull(resultado.Exception);
    }
}
