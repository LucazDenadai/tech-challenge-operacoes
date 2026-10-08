using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Operacoes.Domain.Catalogo;
using OficinaMecanica.Operacoes.Domain.Estoque;

namespace OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Seed;

// Catálogo e saldos de demonstração. Sem dados da Fase 3 (emenda "Dados da Fase 3 e usuários" do ADR-015).
// Idempotente: cada registro é identificado pela chave única e só é inserido se ausente.
public static class SeedDemonstracao
{
    // Mesmo Id do seed do OS (emenda "Filiais em Operações" do ADR-015).
    public static readonly Guid FilialDemoId = Guid.Parse("378aeb39-37f6-43c1-9526-5b1a9fadd553");
    public const string CodigoFilial = "FILIAL-DEMO";

    private static readonly (string Codigo, string Nome, decimal Preco, int Saldo)[] _pecas =
    [
        ("FLT-OLEO-01", "Filtro de óleo", 45.90m, 20),
        ("OLEO-5W30-1L", "Óleo de motor 5W30 (1 L)", 39.90m, 60),
        ("PST-FREIO-D", "Pastilha de freio dianteira (jogo)", 189.00m, 8),
        ("FLT-AR-01", "Filtro de ar", 59.90m, 12),
        ("VELA-IGN-01", "Vela de ignição", 32.50m, 0)
    ];

    private static readonly (string Nome, string Descricao, decimal Preco, int Minutos)[] _servicos =
    [
        ("Diagnóstico", "Avaliação inicial do veículo", 120.00m, 60),
        ("Troca de óleo e filtro", "Inclui mão de obra; peças cobradas à parte", 80.00m, 40),
        ("Troca de pastilhas de freio", "Eixo dianteiro", 150.00m, 90),
        ("Alinhamento e balanceamento", "Quatro rodas", 140.00m, 60)
    ];

    public static async Task ExecutarAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (!await db.Filiais.AnyAsync(f => f.Id == FilialDemoId, ct))
            db.Filiais.Add(new FilialEstoque(FilialDemoId, CodigoFilial));

        foreach (var (codigo, nome, preco, quantidade) in _pecas)
        {
            var peca = await db.Pecas.FirstOrDefaultAsync(p => p.Codigo == codigo, ct);
            if (peca is null)
            {
                peca = new Peca(codigo, nome, string.Empty, preco);
                db.Pecas.Add(peca);
            }

            // A vela fica sem saldo de propósito, para demonstrar a reserva recusada.
            if (!await db.Saldos.AnyAsync(s => s.FilialId == FilialDemoId && s.PecaId == peca.Id, ct))
            {
                var saldo = new SaldoEstoque(FilialDemoId, peca.Id);
                db.Saldos.Add(saldo);
                if (quantidade > 0)
                    db.Movimentacoes.Add(saldo.RegistrarEntrada(quantidade, "Saldo inicial de demonstração"));
            }
        }

        foreach (var (nome, descricao, preco, minutos) in _servicos)
        {
            if (!await db.Servicos.AnyAsync(s => s.Nome == nome, ct))
                db.Servicos.Add(new Servico(nome, descricao, preco, minutos));
        }

        await db.SaveChangesAsync(ct);
    }
}
