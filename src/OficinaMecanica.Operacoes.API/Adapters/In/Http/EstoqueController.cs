using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Application.UseCases.Estoque;

namespace OficinaMecanica.Operacoes.API.Adapters.In.Http;

// Saldo, disponibilidade e movimentações por filial. Reserva, consumo e liberação não têm rota:
// vêm só pelos comandos da Saga (ADR-017).
[ApiController]
[Route("operacoes/estoque")]
[Authorize(Roles = Perfis.Funcionarios)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class EstoqueController(ConsultarEstoqueUseCase consultar, MovimentarEstoqueUseCase movimentar) : ControllerBase
{
    /// <summary>Lista as filiais onde Operações opera estoque. O Id é o filialId do OS.</summary>
    [HttpGet("filiais")]
    [ProducesResponseType(typeof(IEnumerable<FilialEstoqueResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarFiliais(CancellationToken ct) => Ok(await consultar.ListarFiliaisAsync(ct));

    /// <summary>Saldos da filial: disponível e reservado por peça.</summary>
    [HttpGet("filiais/{filialId:guid}/saldos")]
    [ProducesResponseType(typeof(IEnumerable<SaldoResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListarSaldos(Guid filialId, CancellationToken ct)
        => Ok(await consultar.ListarSaldosAsync(filialId, ct));

    /// <summary>Movimentações da filial, mais recentes primeiro. Filtra por peça, OS ou correlação da Saga.</summary>
    [HttpGet("filiais/{filialId:guid}/movimentacoes")]
    [Authorize(Roles = Perfis.AdminEAtendente)]
    [ProducesResponseType(typeof(IEnumerable<MovimentacaoResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListarMovimentacoes(Guid filialId, [FromQuery] Guid? pecaId, [FromQuery] Guid? osId,
        [FromQuery] Guid? correlationId, [FromQuery] int limite = 100, CancellationToken ct = default)
        => Ok(await consultar.ListarMovimentacoesAsync(new FiltroMovimentacao(filialId, pecaId, osId, correlationId, limite), ct));

    /// <summary>Consulta disponibilidade na filial. Só leitura: não reserva.</summary>
    [HttpPost("disponibilidade")]
    [ProducesResponseType(typeof(DisponibilidadeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ConsultarDisponibilidade([FromBody] DisponibilidadeRequest request, CancellationToken ct)
        => Ok(await consultar.ConsultarDisponibilidadeAsync(request, ct));

    /// <summary>Registra entrada de peças na filial (compra, devolução ao fornecedor desfeita etc.).</summary>
    [HttpPost("entradas")]
    [Authorize(Roles = Perfis.Admin)]
    [ProducesResponseType(typeof(SaldoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RegistrarEntrada([FromBody] EntradaEstoqueRequest request, CancellationToken ct)
        => Ok(await movimentar.RegistrarEntradaAsync(request, ct));

    /// <summary>Ajuste de inventário: define o disponível contado. O reservado não muda.</summary>
    [HttpPost("ajustes")]
    [Authorize(Roles = Perfis.Admin)]
    [ProducesResponseType(typeof(SaldoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Ajustar([FromBody] AjusteEstoqueRequest request, CancellationToken ct)
        => Ok(await movimentar.AjustarAsync(request, ct));
}
