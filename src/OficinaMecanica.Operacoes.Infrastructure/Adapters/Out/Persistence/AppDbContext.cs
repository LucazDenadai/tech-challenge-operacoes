using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Operacoes.Domain.Catalogo;
using OficinaMecanica.Operacoes.Domain.Estoque;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Mensageria;

namespace OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence;

// PostgreSQL dedicado de Operações: só catálogo e estoque (ADR-016). Execução fica no DynamoDB (CARD-38b).
public class AppDbContext : DbContext
{
    [ExcludeFromCodeCoverage]
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Peca> Pecas => Set<Peca>();
    public DbSet<Servico> Servicos => Set<Servico>();
    public DbSet<FilialEstoque> Filiais => Set<FilialEstoque>();
    public DbSet<SaldoEstoque> Saldos => Set<SaldoEstoque>();
    public DbSet<Reserva> Reservas => Set<Reserva>();
    public DbSet<MovimentacaoEstoque> Movimentacoes => Set<MovimentacaoEstoque>();
    [ExcludeFromCodeCoverage] public DbSet<MensagemInbox> Inbox => Set<MensagemInbox>();
    public DbSet<MensagemOutbox> Outbox => Set<MensagemOutbox>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
