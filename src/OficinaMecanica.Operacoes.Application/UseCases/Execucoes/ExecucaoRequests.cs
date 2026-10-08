using System.ComponentModel.DataAnnotations;
using OficinaMecanica.Operacoes.Application.Validators;
using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.Application.UseCases.Execucoes;

public class RegistrarDiagnosticoRequest
{
    [Required(ErrorMessage = "Os itens são obrigatórios.")]
    [MinLength(1, ErrorMessage = "Informe ao menos um item.")]
    public List<ItemDiagnosticoRequest> Itens { get; set; } = [];
}

public class ItemDiagnosticoRequest
{
    [EnumDataType(typeof(TipoItemDiagnostico), ErrorMessage = "Tipo deve ser Peca ou Servico.")]
    public TipoItemDiagnostico Tipo { get; set; }

    [IdObrigatorio]
    public Guid ItemId { get; set; }

    [Range(1, 1000, ErrorMessage = "A quantidade deve estar entre 1 e 1.000.")]
    public int Quantidade { get; set; }
}

public class MotivoRequest
{
    [Required(ErrorMessage = "O motivo é obrigatório.")]
    [MaxLength(300, ErrorMessage = "O motivo deve ter no máximo 300 caracteres.")]
    public string Motivo { get; set; } = string.Empty;
}

public class EtapaRequest
{
    [Required(ErrorMessage = "A descrição é obrigatória.")]
    [MaxLength(300, ErrorMessage = "A descrição deve ter no máximo 300 caracteres.")]
    public string Descricao { get; set; } = string.Empty;
}

public class ConclusaoRequest
{
    public List<PecaConsumidaRequest> PecasConsumidas { get; set; } = [];
}

public class FalhaRequest
{
    [Required(ErrorMessage = "O motivo é obrigatório.")]
    [MaxLength(300, ErrorMessage = "O motivo deve ter no máximo 300 caracteres.")]
    public string Motivo { get; set; } = string.Empty;

    public List<PecaConsumidaRequest> PecasConsumidas { get; set; } = [];
}

public class PecaConsumidaRequest
{
    [IdObrigatorio]
    public Guid PecaId { get; set; }

    [Range(0, 1000, ErrorMessage = "A quantidade deve estar entre 0 e 1.000.")]
    public int Quantidade { get; set; }
}

public record ExecucaoResponse(
    Guid Id,
    Guid OsId,
    Guid FilialId,
    Guid VeiculoId,
    StatusExecucao Status,
    DateTime CriadaEm,
    DateTime AtualizadaEm,
    DateTime? DiagnosticadaEm,
    DateTime? EnfileiradaEm,
    DateTime? EncerradaEm,
    Guid? ReservaId,
    string? Motivo,
    IReadOnlyList<ItemDiagnostico> Itens,
    IReadOnlyList<EtapaReparo> Etapas,
    IReadOnlyList<PecaConsumida> PecasConsumidas,
    IReadOnlyList<TransicaoExecucao> Historico)
{
    public static ExecucaoResponse De(Execucao e) => new(
        e.Id, e.OsId, e.FilialId, e.VeiculoId, e.Status, e.CriadaEm, e.AtualizadaEm, e.DiagnosticadaEm, e.EnfileiradaEm,
        e.EncerradaEm, e.ReservaId, e.Motivo, e.Itens, e.Etapas, e.Consumidas, e.Historico);
}
