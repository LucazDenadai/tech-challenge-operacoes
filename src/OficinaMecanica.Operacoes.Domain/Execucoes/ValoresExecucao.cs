using OficinaMecanica.Operacoes.Domain.Comum;

namespace OficinaMecanica.Operacoes.Domain.Execucoes;

// Item do diagnóstico com o preço vigente no catálogo no momento do registro (snapshot, ADR-015).
public sealed record ItemDiagnostico(Guid ItemId, TipoItemDiagnostico Tipo, string Descricao, int Quantidade, decimal PrecoUnitario)
{
    internal void Validar()
    {
        if (ItemId == Guid.Empty)
            throw new ArgumentException("O item do diagnóstico precisa de Id.");
        if (string.IsNullOrWhiteSpace(Descricao))
            throw new ArgumentException("O item do diagnóstico precisa de descrição.");
        if (Quantidade < 1)
            throw new ArgumentException("A quantidade do item do diagnóstico deve ser maior que zero.");
        Dinheiro.Validar(PrecoUnitario, nameof(PrecoUnitario));
    }
}

public sealed record EtapaReparo(string Descricao, string Responsavel, DateTime RegistradaEm);

// Trilha de auditoria: toda mudança de estado, com quem fez e por quê.
public sealed record TransicaoExecucao(StatusExecucao? De, StatusExecucao Para, DateTime Em, string Responsavel, string? Motivo);

public sealed record PecaConsumida(Guid PecaId, int Quantidade);
