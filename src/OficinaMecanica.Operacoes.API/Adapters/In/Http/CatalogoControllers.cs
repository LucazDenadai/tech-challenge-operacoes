using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OficinaMecanica.Operacoes.Application.UseCases.Catalogo;

namespace OficinaMecanica.Operacoes.API.Adapters.In.Http;

[ApiController]
[Route("operacoes/pecas")]
[Authorize(Roles = Perfis.Funcionarios)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class PecasController(GerenciarPecaUseCase useCase) : ControllerBase
{
    /// <summary>Lista o catálogo de peças com preço de tabela. Inativas só com incluirInativas=true.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PecaResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar([FromQuery] bool incluirInativas, CancellationToken ct)
        => Ok(await useCase.ListarAsync(incluirInativas, ct));

    /// <summary>Obtém uma peça do catálogo.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PecaResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(Guid id, CancellationToken ct) => Ok(await useCase.ObterAsync(id, ct));

    /// <summary>Cadastra uma peça. Código repetido retorna 422. O saldo é lançado por filial em /operacoes/estoque/entradas.</summary>
    [HttpPost]
    [Authorize(Roles = Perfis.Admin)]
    [ProducesResponseType(typeof(PecaResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Criar([FromBody] CriarPecaRequest request, CancellationToken ct)
    {
        var peca = await useCase.CriarAsync(request, ct);
        return CreatedAtAction(nameof(Obter), new { id = peca.Id }, peca);
    }

    /// <summary>Atualiza nome, descrição e preço de tabela. Orçamentos já emitidos não mudam (snapshot, ADR-015).</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = Perfis.Admin)]
    [ProducesResponseType(typeof(PecaResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Atualizar(Guid id, [FromBody] AtualizarPecaRequest request, CancellationToken ct)
        => Ok(await useCase.AtualizarAsync(id, request, ct));

    /// <summary>Desativa a peça. Não apaga: movimentações antigas continuam referenciando o item.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Perfis.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Desativar(Guid id, CancellationToken ct)
    {
        await useCase.DesativarAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("operacoes/servicos")]
[Authorize(Roles = Perfis.Funcionarios)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class ServicosController(GerenciarServicoUseCase useCase) : ControllerBase
{
    /// <summary>Lista o catálogo de serviços (mão de obra). Inativos só com incluirInativos=true.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ServicoResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar([FromQuery] bool incluirInativos, CancellationToken ct)
        => Ok(await useCase.ListarAsync(incluirInativos, ct));

    /// <summary>Obtém um serviço do catálogo.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ServicoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(Guid id, CancellationToken ct) => Ok(await useCase.ObterAsync(id, ct));

    /// <summary>Cadastra um serviço.</summary>
    [HttpPost]
    [Authorize(Roles = Perfis.Admin)]
    [ProducesResponseType(typeof(ServicoResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Criar([FromBody] ServicoRequest request, CancellationToken ct)
    {
        var servico = await useCase.CriarAsync(request, ct);
        return CreatedAtAction(nameof(Obter), new { id = servico.Id }, servico);
    }

    /// <summary>Atualiza um serviço.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = Perfis.Admin)]
    [ProducesResponseType(typeof(ServicoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Atualizar(Guid id, [FromBody] ServicoRequest request, CancellationToken ct)
        => Ok(await useCase.AtualizarAsync(id, request, ct));

    /// <summary>Desativa o serviço.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Perfis.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Desativar(Guid id, CancellationToken ct)
    {
        await useCase.DesativarAsync(id, ct);
        return NoContent();
    }
}

// Perfis do JWT emitido pelo OS (emenda "Dados da Fase 3 e usuários" do ADR-015). Token de cliente (Lambda) não acessa Operações.
public static class Perfis
{
    public const string Admin = "Admin";
    public const string Funcionarios = "Admin,Atendente,Mecanico";
    public const string AdminEAtendente = "Admin,Atendente";
    public const string Tecnicos = "Admin,Mecanico";
}
