using System.Diagnostics.CodeAnalysis;

namespace OficinaMecanica.Operacoes.Domain.Estoque;

public class ItemReserva
{
    public Guid ReservaId { get; private set; }
    public Guid PecaId { get; private set; }
    public int Quantidade { get; private set; }
    public int QuantidadeConsumida { get; private set; }
    public int QuantidadeLiberada { get; private set; }

    public int QuantidadePendente => Quantidade - QuantidadeConsumida - QuantidadeLiberada;

    [ExcludeFromCodeCoverage]
    protected ItemReserva() { }

    internal ItemReserva(Guid reservaId, Guid pecaId, int quantidade)
    {
        ReservaId = reservaId;
        PecaId = pecaId;
        Quantidade = quantidade;
    }

    internal void RegistrarConsumo(int quantidade) => QuantidadeConsumida += quantidade;

    internal void RegistrarLiberacao(int quantidade) => QuantidadeLiberada += quantidade;
}
