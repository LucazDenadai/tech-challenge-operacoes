using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OficinaMecanica.Operacoes.Application.UseCases.Execucoes;
using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.API.Adapters.In.Http;

// Execução da OS (CARD-38b). A execução nasce do DiagnosisRequested e entra na fila pelo
// ExecutionStartRequested (ADR-017); por aqui o técnico registra diagnóstico, reparo, conclusão e falha.
[ApiController]
[Route("operacoes/execucoes")]
[Authorize(Roles = Perfis.Funcionarios)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class ExecucoesController(ConsultarExecucoesUseCase consultar, AcoesExecucaoUseCase acoes) : ControllerBase
{
    /// <summary>Fila da filial em um estado, na ordem da última atualização.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ExecucaoResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListarFila([FromQuery] Guid filialId, [FromQuery] StatusExecucao status, CancellationToken ct)
    {
        if (filialId == Guid.Empty)
        {
            ModelState.AddModelError(nameof(filialId), "O filialId é obrigatório.");
            return ValidationProblem(ModelState);
        }
        return Ok(await consultar.ListarFilaAsync(filialId, status, ct));
    }

    /// <summary>Detalhe da execução, com itens, etapas, consumo e histórico de transições.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ExecucaoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(Guid id, CancellationToken ct) => Ok(await consultar.ObterAsync(id, ct));

    /// <summary>Execução de uma OS. Há no máximo uma por OS.</summary>
    [HttpGet("os/{osId:guid}")]
    [ProducesResponseType(typeof(ExecucaoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ObterPorOs(Guid osId, CancellationToken ct) => Ok(await consultar.ObterPorOsAsync(osId, ct));

    /// <summary>Registra peças e serviços. O preço vem do catálogo e segue no DiagnosisCompleted.</summary>
    [HttpPost("{id:guid}/diagnostico")]
    [Authorize(Roles = Perfis.Tecnicos)]
    [ProducesResponseType(typeof(ExecucaoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RegistrarDiagnostico(Guid id, [FromBody] RegistrarDiagnosticoRequest request, CancellationToken ct)
        => Ok(await acoes.RegistrarDiagnosticoAsync(id, request, Responsavel(), ct));

    /// <summary>Rejeita o diagnóstico com motivo (DiagnosisRejected). A Saga cancela a OS.</summary>
    [HttpPost("{id:guid}/diagnostico/rejeicao")]
    [Authorize(Roles = Perfis.Tecnicos)]
    [ProducesResponseType(typeof(ExecucaoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RejeitarDiagnostico(Guid id, [FromBody] MotivoRequest request, CancellationToken ct)
        => Ok(await acoes.RejeitarDiagnosticoAsync(id, request.Motivo, Responsavel(), ct));

    /// <summary>Tira a execução da fila e inicia o reparo. Sem evento para o OS.</summary>
    [HttpPost("{id:guid}/reparo")]
    [Authorize(Roles = Perfis.Tecnicos)]
    [ProducesResponseType(typeof(ExecucaoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> IniciarReparo(Guid id, CancellationToken ct)
        => Ok(await acoes.IniciarReparoAsync(id, Responsavel(), ct));

    /// <summary>Registra uma etapa do reparo. Progresso consultável aqui, sem evento para o OS.</summary>
    [HttpPost("{id:guid}/etapas")]
    [Authorize(Roles = Perfis.Tecnicos)]
    [ProducesResponseType(typeof(ExecucaoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RegistrarEtapa(Guid id, [FromBody] EtapaRequest request, CancellationToken ct)
        => Ok(await acoes.RegistrarEtapaAsync(id, request.Descricao, Responsavel(), ct));

    /// <summary>Conclui o reparo: consome da reserva o que foi usado, devolve a sobra e publica ExecutionCompleted.</summary>
    [HttpPost("{id:guid}/conclusao")]
    [Authorize(Roles = Perfis.Tecnicos)]
    [ProducesResponseType(typeof(ExecucaoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Concluir(Guid id, [FromBody] ConclusaoRequest request, CancellationToken ct)
        => Ok(await acoes.ConcluirAsync(id, request.PecasConsumidas, Responsavel(), ct));

    /// <summary>Registra a falha com o consumo real e publica ExecutionFailed. A Saga compensa (ADR-017).</summary>
    [HttpPost("{id:guid}/falha")]
    [Authorize(Roles = Perfis.Tecnicos)]
    [ProducesResponseType(typeof(ExecucaoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RegistrarFalha(Guid id, [FromBody] FalhaRequest request, CancellationToken ct)
        => Ok(await acoes.RegistrarFalhaAsync(id, request.Motivo, request.PecasConsumidas, Responsavel(), ct));

    // Quem fez a ação, para a trilha de auditoria: e-mail do JWT do OS, ou o sub.
    private string Responsavel()
        => User.FindFirstValue(ClaimTypes.Email)
           ?? User.FindFirstValue(JwtRegisteredClaimNames.Email)
           ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? "desconhecido";
}
