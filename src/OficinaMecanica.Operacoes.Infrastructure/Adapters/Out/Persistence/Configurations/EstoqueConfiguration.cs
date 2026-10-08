using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Operacoes.Domain.Catalogo;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Configurations;

public class FilialEstoqueConfiguration : IEntityTypeConfiguration<FilialEstoque>
{
    public void Configure(EntityTypeBuilder<FilialEstoque> builder)
    {
        builder.ToTable("Filiais");
        builder.HasKey(f => f.Id);
        // O Id vem do OS (emenda "Filiais em Operações" do ADR-015).
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.Codigo).HasMaxLength(30).IsRequired();
        builder.HasIndex(f => f.Codigo).IsUnique();
    }
}

public class SaldoEstoqueConfiguration : IEntityTypeConfiguration<SaldoEstoque>
{
    // Coluna de sistema xmin do PostgreSQL como token de concorrência: duas gravações sobre o
    // mesmo saldo lido na mesma versão não passam; a segunda recebe conflito e refaz a operação.
    public const string Versao = "Versao";

    public void Configure(EntityTypeBuilder<SaldoEstoque> builder)
    {
        builder.ToTable("Saldos", t =>
        {
            t.HasCheckConstraint("CK_Saldos_Disponivel", "\"QuantidadeDisponivel\" >= 0");
            t.HasCheckConstraint("CK_Saldos_Reservada", "\"QuantidadeReservada\" >= 0");
        });
        builder.HasKey(s => new { s.FilialId, s.PecaId });
        builder.Property<uint>(Versao).IsRowVersion();
        builder.HasOne<FilialEstoque>().WithMany().HasForeignKey(s => s.FilialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Peca>().WithMany().HasForeignKey(s => s.PecaId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ReservaConfiguration : IEntityTypeConfiguration<Reserva>
{
    public void Configure(EntityTypeBuilder<Reserva> builder)
    {
        builder.ToTable("Reservas");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.HasIndex(r => r.IdempotencyKey).IsUnique();
        builder.HasIndex(r => r.OsId);
        builder.Property(r => r.Status).IsRequired();
        builder.Property<uint>(SaldoEstoqueConfiguration.Versao).IsRowVersion();
        builder.HasOne<FilialEstoque>().WithMany().HasForeignKey(r => r.FilialId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(r => r.Itens).WithOne().HasForeignKey(i => i.ReservaId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Itens).HasField("_itens").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class ItemReservaConfiguration : IEntityTypeConfiguration<ItemReserva>
{
    public void Configure(EntityTypeBuilder<ItemReserva> builder)
    {
        builder.ToTable("ItensReserva", t =>
            t.HasCheckConstraint("CK_ItensReserva_Quantidades",
                "\"QuantidadeConsumida\" >= 0 AND \"QuantidadeLiberada\" >= 0 AND \"QuantidadeConsumida\" + \"QuantidadeLiberada\" <= \"Quantidade\""));
        builder.HasKey(i => new { i.ReservaId, i.PecaId });
        builder.Ignore(i => i.QuantidadePendente);
        builder.HasOne<Peca>().WithMany().HasForeignKey(i => i.PecaId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class MovimentacaoEstoqueConfiguration : IEntityTypeConfiguration<MovimentacaoEstoque>
{
    public void Configure(EntityTypeBuilder<MovimentacaoEstoque> builder)
    {
        builder.ToTable("Movimentacoes");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Tipo).IsRequired();
        builder.Property(m => m.Motivo).HasMaxLength(200).IsRequired();
        builder.HasIndex(m => new { m.FilialId, m.PecaId, m.OcorridoEm });
        builder.HasIndex(m => m.OsId);
        builder.HasIndex(m => m.CorrelationId);
        builder.HasOne<FilialEstoque>().WithMany().HasForeignKey(m => m.FilialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Peca>().WithMany().HasForeignKey(m => m.PecaId).OnDelete(DeleteBehavior.Restrict);
    }
}
