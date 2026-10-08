namespace OficinaMecanica.Operacoes.Domain.Estoque;

// Peça e quantidade, no formato do schema PecaQuantity do AsyncAPI.
public sealed record ItemQuantidade(Guid PecaId, int Quantidade)
{
    // Junta linhas repetidas da mesma peça, somando as quantidades.
    public static IReadOnlyList<ItemQuantidade> Consolidar(IEnumerable<ItemQuantidade> itens)
        => itens.GroupBy(i => i.PecaId)
            .Select(g => new ItemQuantidade(g.Key, g.Sum(i => i.Quantidade)))
            .ToList();
}
