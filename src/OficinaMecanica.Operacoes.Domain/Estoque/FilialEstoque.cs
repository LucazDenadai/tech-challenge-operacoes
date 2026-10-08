using System.Diagnostics.CodeAnalysis;
using OficinaMecanica.Operacoes.Domain.Comum;

namespace OficinaMecanica.Operacoes.Domain.Estoque;

// Filial onde Operações opera estoque. O Id é o filialId do OS; nome e demais dados
// ficam só no cadastro mestre do OS (emenda "Filiais em Operações" do ADR-015).
public class FilialEstoque : EntityBase
{
    public string Codigo { get; private set; } = string.Empty;
    public bool Ativo { get; private set; } = true;

    [ExcludeFromCodeCoverage]
    protected FilialEstoque() { }

    public FilialEstoque(Guid filialId, string codigo)
    {
        if (filialId == Guid.Empty)
            throw new ArgumentException("O Id da filial é obrigatório.", nameof(filialId));
        if (string.IsNullOrWhiteSpace(codigo))
            throw new ArgumentException("O código da filial é obrigatório.", nameof(codigo));

        Id = filialId;
        Codigo = codigo.Trim().ToUpperInvariant();
    }

    public void Desativar()
    {
        Ativo = false;
        MarcarAtualizado();
    }
}
