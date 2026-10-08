using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Operacoes.Domain.Catalogo;

namespace OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Configurations;

public class PecaConfiguration : IEntityTypeConfiguration<Peca>
{
    public void Configure(EntityTypeBuilder<Peca> builder)
    {
        builder.ToTable("Pecas");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Codigo).HasMaxLength(30).IsRequired();
        builder.HasIndex(p => p.Codigo).IsUnique();
        builder.Property(p => p.Nome).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Descricao).HasMaxLength(500).IsRequired();
        builder.Property(p => p.PrecoTabela).HasPrecision(12, 2);
    }
}

public class ServicoConfiguration : IEntityTypeConfiguration<Servico>
{
    public void Configure(EntityTypeBuilder<Servico> builder)
    {
        builder.ToTable("Servicos");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Nome).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Descricao).HasMaxLength(500).IsRequired();
        builder.Property(s => s.Preco).HasPrecision(12, 2);
    }
}
