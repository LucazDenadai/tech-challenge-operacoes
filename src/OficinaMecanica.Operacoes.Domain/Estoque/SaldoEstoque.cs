using System.Diagnostics.CodeAnalysis;

namespace OficinaMecanica.Operacoes.Domain.Estoque;

// Saldo de uma peça em uma filial (ADR-015). Disponível e reservado nunca ficam negativos.
// Cada mudança devolve a movimentação que a registra.
public class SaldoEstoque
{
    public Guid FilialId { get; private set; }
    public Guid PecaId { get; private set; }
    public int QuantidadeDisponivel { get; private set; }
    public int QuantidadeReservada { get; private set; }
    public DateTime? AtualizadoEm { get; private set; }

    [ExcludeFromCodeCoverage]
    protected SaldoEstoque() { }

    public SaldoEstoque(Guid filialId, Guid pecaId)
    {
        if (filialId == Guid.Empty)
            throw new ArgumentException("A filial do saldo é obrigatória.", nameof(filialId));
        if (pecaId == Guid.Empty)
            throw new ArgumentException("A peça do saldo é obrigatória.", nameof(pecaId));

        FilialId = filialId;
        PecaId = pecaId;
    }

    public bool PodeReservar(int quantidade) => quantidade > 0 && QuantidadeDisponivel >= quantidade;

    public MovimentacaoEstoque RegistrarEntrada(int quantidade, string motivo)
    {
        ExigirPositiva(quantidade);
        QuantidadeDisponivel += quantidade;
        return Movimentar(TipoMovimentacao.Entrada, quantidade, motivo, null);
    }

    // Ajuste de inventário: define o disponível contado. O reservado não muda.
    public MovimentacaoEstoque? Ajustar(int quantidadeDisponivelContada, string motivo)
    {
        if (quantidadeDisponivelContada < 0)
            throw new ArgumentException("A quantidade contada não pode ser negativa.", nameof(quantidadeDisponivelContada));

        var diferenca = quantidadeDisponivelContada - QuantidadeDisponivel;
        if (diferenca == 0)
            return null;

        QuantidadeDisponivel = quantidadeDisponivelContada;
        return Movimentar(TipoMovimentacao.Ajuste, diferenca, motivo, null);
    }

    internal MovimentacaoEstoque Reservar(int quantidade, ReferenciaSaga referencia)
    {
        ExigirPositiva(quantidade);
        if (QuantidadeDisponivel < quantidade)
            throw new InvalidOperationException($"Saldo insuficiente da peça '{PecaId}' na filial '{FilialId}'.");

        QuantidadeDisponivel -= quantidade;
        QuantidadeReservada += quantidade;
        return Movimentar(TipoMovimentacao.Reserva, quantidade, "Reserva para a OS", referencia);
    }

    internal MovimentacaoEstoque Consumir(int quantidade, ReferenciaSaga referencia)
    {
        ExigirPositiva(quantidade);
        ExigirReservado(quantidade);

        QuantidadeReservada -= quantidade;
        return Movimentar(TipoMovimentacao.Consumo, quantidade, "Consumo na execução da OS", referencia);
    }

    internal MovimentacaoEstoque Liberar(int quantidade, string motivo, ReferenciaSaga referencia)
    {
        ExigirPositiva(quantidade);
        ExigirReservado(quantidade);

        QuantidadeReservada -= quantidade;
        QuantidadeDisponivel += quantidade;
        return Movimentar(TipoMovimentacao.Liberacao, quantidade, motivo, referencia);
    }

    private MovimentacaoEstoque Movimentar(TipoMovimentacao tipo, int quantidade, string motivo, ReferenciaSaga? referencia)
    {
        AtualizadoEm = DateTime.UtcNow;
        return new MovimentacaoEstoque(FilialId, PecaId, tipo, quantidade, motivo, referencia);
    }

    private static void ExigirPositiva(int quantidade)
    {
        if (quantidade <= 0)
            throw new ArgumentException("A quantidade deve ser maior que zero.", nameof(quantidade));
    }

    private void ExigirReservado(int quantidade)
    {
        if (QuantidadeReservada < quantidade)
            throw new InvalidOperationException($"Quantidade reservada insuficiente da peça '{PecaId}' na filial '{FilialId}'.");
    }
}
