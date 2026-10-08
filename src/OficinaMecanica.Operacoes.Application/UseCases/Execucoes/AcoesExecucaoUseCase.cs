using OficinaMecanica.Operacoes.Application.Contratos.Saga;
using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.In;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Estoque;
using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.Application.UseCases.Execucoes;

// Ações do técnico sobre a execução (API). Diagnóstico, conclusão e falha publicam evento para a Saga;
// início do reparo e etapas ficam só em Operações (emenda "Progresso da execução" do ADR-015).
public class AcoesExecucaoUseCase(IExecucaoRepository repositorio, ICatalogoParaExecucao catalogo, IEstoqueParaExecucao estoque)
{
    public async Task<ExecucaoResponse> RegistrarDiagnosticoAsync(Guid id, RegistrarDiagnosticoRequest request, string responsavel, CancellationToken ct = default)
    {
        var execucao = await ObterAsync(id, ct);
        var itens = await catalogo.PrecificarAsync(
            request.Itens.Select(i => new ItemSolicitado(i.Tipo, i.ItemId, i.Quantidade)).ToList(), ct);

        execucao.RegistrarDiagnostico(itens, responsavel);
        var evento = RespostaSaga.Diagnosticado(OrigemDiagnostico(execucao), execucao.Id, execucao.DiagnosticadaEm!.Value,
            itens.Select(i => new PricedItem
            {
                ItemId = i.ItemId, Type = i.Tipo.ToString(), Description = i.Descricao, Quantity = i.Quantidade, UnitPrice = i.PrecoUnitario
            }).ToList());

        return await GravarAsync(execucao, [evento], ct);
    }

    public async Task<ExecucaoResponse> RejeitarDiagnosticoAsync(Guid id, string motivo, string responsavel, CancellationToken ct = default)
    {
        var execucao = await ObterAsync(id, ct);
        execucao.RejeitarDiagnostico(motivo, responsavel);
        return await GravarAsync(execucao, [RespostaSaga.DiagnosticoRejeitado(OrigemDiagnostico(execucao), execucao.Motivo!)], ct);
    }

    public async Task<ExecucaoResponse> IniciarReparoAsync(Guid id, string responsavel, CancellationToken ct = default)
    {
        var execucao = await ObterAsync(id, ct);
        execucao.IniciarReparo(responsavel);
        return await GravarAsync(execucao, [], ct);
    }

    public async Task<ExecucaoResponse> RegistrarEtapaAsync(Guid id, string descricao, string responsavel, CancellationToken ct = default)
    {
        var execucao = await ObterAsync(id, ct);
        execucao.RegistrarEtapa(descricao, responsavel);
        return await GravarAsync(execucao, [], ct);
    }

    // Consome a reserva no PostgreSQL antes de gravar o agregado no DynamoDB. Sem transação entre os dois
    // stores (ADR-016), a repetição após falha da segunda gravação não consome de novo: o consumo é idempotente.
    public async Task<ExecucaoResponse> ConcluirAsync(Guid id, IReadOnlyList<PecaConsumidaRequest> consumidas, string responsavel, CancellationToken ct = default)
    {
        var execucao = await ObterAsync(id, ct);
        var pecas = Pecas(consumidas);
        execucao.ValidarConclusao(pecas);

        await estoque.ConcluirConsumoAsync(execucao.ReservaId!.Value, Itens(pecas), ct);
        execucao.Concluir(pecas, responsavel);

        var evento = RespostaSaga.Concluida(OrigemInicio(execucao), execucao.Id, execucao.EncerradaEm!.Value, Contrato(execucao.Consumidas));
        return await GravarAsync(execucao, [evento], ct);
    }

    public async Task<ExecucaoResponse> RegistrarFalhaAsync(Guid id, string motivo, IReadOnlyList<PecaConsumidaRequest> consumidas, string responsavel, CancellationToken ct = default)
    {
        var execucao = await ObterAsync(id, ct);
        var pecas = Pecas(consumidas);
        execucao.ValidarFalha(motivo, pecas);

        await estoque.RegistrarConsumoComFalhaAsync(execucao.ReservaId!.Value, Itens(pecas), ct);
        execucao.Falhar(motivo, pecas, responsavel);

        var evento = RespostaSaga.Falhou(OrigemInicio(execucao), execucao.Id, execucao.Motivo!, Contrato(execucao.Consumidas));
        return await GravarAsync(execucao, [evento], ct);
    }

    private async Task<Execucao> ObterAsync(Guid id, CancellationToken ct)
        => await repositorio.ObterAsync(id, ct) ?? throw new NotFoundException("Execução", id);

    private async Task<ExecucaoResponse> GravarAsync(Execucao execucao, IReadOnlyList<MensagemSaga> eventos, CancellationToken ct)
    {
        await repositorio.GravarAsync(execucao, eventos, null, ct);
        return ExecucaoResponse.De(execucao);
    }

    private static OrigemEvento OrigemDiagnostico(Execucao e) => new(e.CorrelationId, e.ComandoDiagnosticoId, e.OsId, e.FilialId);

    private static OrigemEvento OrigemInicio(Execucao e) => new(e.CorrelationId, e.ComandoInicioId, e.OsId, e.FilialId);

    private static IReadOnlyList<PecaConsumida> Pecas(IReadOnlyList<PecaConsumidaRequest> consumidas)
        => consumidas.Select(c => new PecaConsumida(c.PecaId, c.Quantidade)).ToList();

    private static IReadOnlyList<ItemQuantidade> Itens(IReadOnlyList<PecaConsumida> pecas)
        => pecas.Select(p => new ItemQuantidade(p.PecaId, p.Quantidade)).ToList();

    private static IReadOnlyList<PecaQuantity> Contrato(IReadOnlyList<PecaConsumida> pecas)
        => pecas.Select(p => new PecaQuantity { PecaId = p.PecaId, Quantity = p.Quantidade }).ToList();
}
