using System.Diagnostics.CodeAnalysis;
using OficinaMecanica.Operacoes.Domain.Comum;

namespace OficinaMecanica.Operacoes.Domain.Catalogo;

// Mão de obra, vinda do Atendimento da Fase 3 (emenda "Catálogo de serviços" do ADR-015).
public class Servico : EntityBase
{
    public string Nome { get; private set; } = string.Empty;
    public string Descricao { get; private set; } = string.Empty;
    public decimal Preco { get; private set; }
    public int TempoConclusaoMinutos { get; private set; }
    public bool Ativo { get; private set; } = true;

    [ExcludeFromCodeCoverage]
    protected Servico() { }

    public Servico(string nome, string descricao, decimal preco, int tempoConclusaoMinutos)
        => Definir(nome, descricao, preco, tempoConclusaoMinutos);

    public void Atualizar(string nome, string descricao, decimal preco, int tempoConclusaoMinutos)
    {
        Definir(nome, descricao, preco, tempoConclusaoMinutos);
        MarcarAtualizado();
    }

    public void Desativar()
    {
        Ativo = false;
        MarcarAtualizado();
    }

    private void Definir(string nome, string descricao, decimal preco, int tempoConclusaoMinutos)
    {
        if (string.IsNullOrWhiteSpace(nome))
            throw new ArgumentException("O nome do serviço é obrigatório.", nameof(nome));
        if (tempoConclusaoMinutos <= 0)
            throw new ArgumentException("O tempo de conclusão deve ser maior que zero.", nameof(tempoConclusaoMinutos));

        Nome = nome.Trim();
        Descricao = descricao?.Trim() ?? string.Empty;
        Preco = Dinheiro.Validar(preco, nameof(preco));
        TempoConclusaoMinutos = tempoConclusaoMinutos;
    }
}
