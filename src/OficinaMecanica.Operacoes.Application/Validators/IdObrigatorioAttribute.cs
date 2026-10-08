using System.ComponentModel.DataAnnotations;

namespace OficinaMecanica.Operacoes.Application.Validators;

// [Required] não rejeita Guid ausente no JSON, que chega como Guid.Empty.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class IdObrigatorioAttribute : ValidationAttribute
{
    public IdObrigatorioAttribute() : base("O campo {0} é obrigatório.") { }

    public override bool IsValid(object? value) => value is Guid id && id != Guid.Empty;
}
