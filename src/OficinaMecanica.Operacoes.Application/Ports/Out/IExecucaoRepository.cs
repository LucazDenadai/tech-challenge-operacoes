using OficinaMecanica.Operacoes.Application.Contratos.Saga;
using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.Application.Ports.Out;

public sealed record MensagemRecebida(MensagemSaga Mensagem, string Canal, string Payload);

// Store do agregado de execução: DynamoDB (ADR-016).
public interface IExecucaoRepository
{
    Task<Execucao?> ObterAsync(Guid executionId, CancellationToken ct = default);
    Task<Execucao?> ObterPorOsAsync(Guid osId, CancellationToken ct = default);
    Task<IReadOnlyList<Execucao>> ListarFilaAsync(Guid filialId, StatusExecucao status, CancellationToken ct = default);

    // Grava em uma única transação o agregado (novo ou na versão lida), os eventos na outbox e, quando
    // vier de mensagem, o messageId na inbox. Retorna false se a mensagem já tinha sido recebida (nada é gravado).
    // Execução nova para OS que já tem execução, ou versão alterada por outra gravação: ConflitoConcorrenciaException.
    Task<bool> GravarAsync(Execucao? execucao, IReadOnlyList<MensagemSaga> eventos, MensagemRecebida? recebida = null, CancellationToken ct = default);
}
