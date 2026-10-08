using System.Diagnostics.CodeAnalysis;
using OficinaMecanica.Operacoes.Domain.Comum;

namespace OficinaMecanica.Operacoes.Domain.Estoque;

// Registro imutável de cada mudança de saldo. Só SaldoEstoque cria movimentações,
// para que nenhuma mudança de saldo fique sem registro.
public class MovimentacaoEstoque : EntityBase
{
    public Guid FilialId { get; private set; }
    public Guid PecaId { get; private set; }
    public TipoMovimentacao Tipo { get; private set; }
    // Positiva, exceto no Ajuste, que guarda a diferença com sinal.
    public int Quantidade { get; private set; }
    public string Motivo { get; private set; } = string.Empty;
    public Guid? OsId { get; private set; }
    public Guid? ReservaId { get; private set; }
    public Guid? CorrelationId { get; private set; }
    public DateTime OcorridoEm { get; private set; } = DateTime.UtcNow;

    [ExcludeFromCodeCoverage]
    protected MovimentacaoEstoque() { }

    internal MovimentacaoEstoque(Guid filialId, Guid pecaId, TipoMovimentacao tipo, int quantidade,
        string motivo, ReferenciaSaga? referencia)
    {
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("O motivo da movimentação é obrigatório.", nameof(motivo));

        FilialId = filialId;
        PecaId = pecaId;
        Tipo = tipo;
        Quantidade = quantidade;
        Motivo = motivo.Trim();
        OsId = referencia?.OsId;
        ReservaId = referencia?.ReservaId;
        CorrelationId = referencia?.CorrelationId;
    }
}
