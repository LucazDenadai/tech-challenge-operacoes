using System.Diagnostics.CodeAnalysis;
using OficinaMecanica.Operacoes.Domain.Comum;

namespace OficinaMecanica.Operacoes.Domain.Catalogo;

// Catálogo e preço de tabela da peça. O saldo fica em SaldoEstoque, por filial.
public class Peca : EntityBase
{
    public string Codigo { get; private set; } = string.Empty;
    public string Nome { get; private set; } = string.Empty;
    public string Descricao { get; private set; } = string.Empty;
    public decimal PrecoTabela { get; private set; }
    public bool Ativo { get; private set; } = true;

    [ExcludeFromCodeCoverage]
    protected Peca() { }

    public Peca(string codigo, string nome, string descricao, decimal precoTabela)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            throw new ArgumentException("O código da peça é obrigatório.", nameof(codigo));

        Codigo = codigo.Trim().ToUpperInvariant();
        Definir(nome, descricao, precoTabela);
    }

    public void Atualizar(string nome, string descricao, decimal precoTabela)
    {
        Definir(nome, descricao, precoTabela);
        MarcarAtualizado();
    }

    // Desativar não apaga: movimentações antigas continuam referenciando a peça.
    public void Desativar()
    {
        Ativo = false;
        MarcarAtualizado();
    }

    private void Definir(string nome, string descricao, decimal precoTabela)
    {
        if (string.IsNullOrWhiteSpace(nome))
            throw new ArgumentException("O nome da peça é obrigatório.", nameof(nome));

        Nome = nome.Trim();
        Descricao = descricao?.Trim() ?? string.Empty;
        PrecoTabela = Dinheiro.Validar(precoTabela, nameof(precoTabela));
    }
}
