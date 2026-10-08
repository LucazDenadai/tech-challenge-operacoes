using System.ComponentModel.DataAnnotations;
using OficinaMecanica.Operacoes.Application.Validators;

namespace OficinaMecanica.Operacoes.Application.UseCases.Estoque;

public class EntradaEstoqueRequest
{
    [IdObrigatorio]
    public Guid FilialId { get; set; }

    [IdObrigatorio]
    public Guid PecaId { get; set; }

    [Range(1, 100000, ErrorMessage = "A quantidade deve estar entre 1 e 100.000.")]
    public int Quantidade { get; set; }

    [Required(ErrorMessage = "O motivo é obrigatório.")]
    [MaxLength(200, ErrorMessage = "O motivo deve ter no máximo 200 caracteres.")]
    public string Motivo { get; set; } = string.Empty;
}

public class AjusteEstoqueRequest
{
    [IdObrigatorio]
    public Guid FilialId { get; set; }

    [IdObrigatorio]
    public Guid PecaId { get; set; }

    [Range(0, 100000, ErrorMessage = "A quantidade contada deve estar entre 0 e 100.000.")]
    public int QuantidadeContada { get; set; }

    [Required(ErrorMessage = "O motivo é obrigatório.")]
    [MaxLength(200, ErrorMessage = "O motivo deve ter no máximo 200 caracteres.")]
    public string Motivo { get; set; } = string.Empty;
}

public class DisponibilidadeRequest
{
    [IdObrigatorio]
    public Guid FilialId { get; set; }

    [Required(ErrorMessage = "Os itens são obrigatórios.")]
    [MinLength(1, ErrorMessage = "Informe ao menos um item.")]
    public List<ItemDisponibilidadeRequest> Itens { get; set; } = [];
}

public class ItemDisponibilidadeRequest
{
    [IdObrigatorio]
    public Guid PecaId { get; set; }

    [Range(1, 100000, ErrorMessage = "A quantidade deve estar entre 1 e 100.000.")]
    public int Quantidade { get; set; }
}

public record FilialEstoqueResponse(Guid Id, string Codigo, bool Ativo);

public record SaldoResponse(Guid FilialId, Guid PecaId, string CodigoPeca, string NomePeca, int QuantidadeDisponivel, int QuantidadeReservada);

public record ItemDisponibilidadeResponse(Guid PecaId, int QuantidadeSolicitada, int QuantidadeDisponivel, bool Atende);

public record DisponibilidadeResponse(Guid FilialId, bool Atende, IReadOnlyList<ItemDisponibilidadeResponse> Itens);

public record MovimentacaoResponse(
    Guid Id,
    Guid FilialId,
    Guid PecaId,
    string Tipo,
    int Quantidade,
    string Motivo,
    Guid? OsId,
    Guid? ReservaId,
    Guid? CorrelationId,
    DateTime OcorridoEm);
