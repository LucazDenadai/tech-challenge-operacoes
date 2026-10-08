using System.ComponentModel.DataAnnotations;

namespace OficinaMecanica.Operacoes.Application.UseCases.Catalogo;

public class CriarPecaRequest
{
    [Required(ErrorMessage = "O código é obrigatório.")]
    [MaxLength(30, ErrorMessage = "O código deve ter no máximo 30 caracteres.")]
    public string Codigo { get; set; } = string.Empty;

    [Required(ErrorMessage = "O nome é obrigatório.")]
    [MaxLength(100, ErrorMessage = "O nome deve ter no máximo 100 caracteres.")]
    public string Nome { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "A descrição deve ter no máximo 500 caracteres.")]
    public string Descricao { get; set; } = string.Empty;

    [Range(typeof(decimal), "0.01", "1000000", ErrorMessage = "O preço deve estar entre 0,01 e 1.000.000.")]
    public decimal PrecoTabela { get; set; }
}

public class AtualizarPecaRequest
{
    [Required(ErrorMessage = "O nome é obrigatório.")]
    [MaxLength(100, ErrorMessage = "O nome deve ter no máximo 100 caracteres.")]
    public string Nome { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "A descrição deve ter no máximo 500 caracteres.")]
    public string Descricao { get; set; } = string.Empty;

    [Range(typeof(decimal), "0.01", "1000000", ErrorMessage = "O preço deve estar entre 0,01 e 1.000.000.")]
    public decimal PrecoTabela { get; set; }
}

public class ServicoRequest
{
    [Required(ErrorMessage = "O nome é obrigatório.")]
    [MaxLength(100, ErrorMessage = "O nome deve ter no máximo 100 caracteres.")]
    public string Nome { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "A descrição deve ter no máximo 500 caracteres.")]
    public string Descricao { get; set; } = string.Empty;

    [Range(typeof(decimal), "0.01", "1000000", ErrorMessage = "O preço deve estar entre 0,01 e 1.000.000.")]
    public decimal Preco { get; set; }

    [Range(1, 10080, ErrorMessage = "O tempo de conclusão deve estar entre 1 e 10.080 minutos.")]
    public int TempoConclusaoMinutos { get; set; }
}

public record PecaResponse(Guid Id, string Codigo, string Nome, string Descricao, decimal PrecoTabela, string Moeda, bool Ativo);

public record ServicoResponse(Guid Id, string Nome, string Descricao, decimal Preco, string Moeda, int TempoConclusaoMinutos, bool Ativo);
