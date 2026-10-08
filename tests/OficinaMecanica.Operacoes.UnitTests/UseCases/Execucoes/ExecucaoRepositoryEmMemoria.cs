using OficinaMecanica.Operacoes.Application.Contratos.Saga;
using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.UnitTests.UseCases.Execucoes;

// Repositório em memória com as mesmas regras do DynamoDB: inbox deduplica, uma execução por OS,
// versão conferida na gravação. Guarda cópias, como um banco, para o teste não compartilhar o objeto.
public class ExecucaoRepositoryEmMemoria : IExecucaoRepository
{
    private readonly Dictionary<Guid, EstadoExecucao> _execucoes = new();
    private readonly HashSet<Guid> _inbox = new();

    public List<MensagemSaga> Outbox { get; } = new();
    public int Gravacoes { get; private set; }
    public Exception? FalhaNaProximaGravacao { get; set; }

    public Task<Execucao?> ObterAsync(Guid executionId, CancellationToken ct = default)
        => Task.FromResult(_execucoes.TryGetValue(executionId, out var e) ? Execucao.Reidratar(e) : null);

    public Task<Execucao?> ObterPorOsAsync(Guid osId, CancellationToken ct = default)
        => Task.FromResult(_execucoes.Values.Where(e => e.OsId == osId).Select(Execucao.Reidratar).FirstOrDefault());

    public Task<IReadOnlyList<Execucao>> ListarFilaAsync(Guid filialId, StatusExecucao status, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Execucao>>(_execucoes.Values
            .Where(e => e.FilialId == filialId && e.Status == status)
            .OrderBy(e => e.AtualizadaEm)
            .Select(Execucao.Reidratar)
            .ToList());

    public Task<bool> GravarAsync(Execucao? execucao, IReadOnlyList<MensagemSaga> eventos, MensagemRecebida? recebida = null, CancellationToken ct = default)
    {
        if (FalhaNaProximaGravacao is { } falha)
        {
            FalhaNaProximaGravacao = null;
            throw falha;
        }

        if (recebida is not null && _inbox.Contains(recebida.Mensagem.MessageId))
            return Task.FromResult(false);

        if (execucao is not null)
        {
            var atual = _execucoes.GetValueOrDefault(execucao.Id);
            var versaoEsperada = atual?.Versao ?? 0;
            if (execucao.Versao != versaoEsperada || (atual is null && _execucoes.Values.Any(e => e.OsId == execucao.OsId)))
                throw new ConflitoConcorrenciaException(new Exception("versão ou OS em conflito"));

            execucao.MarcarGravada(versaoEsperada + 1);
            _execucoes[execucao.Id] = execucao.Estado();
        }

        if (recebida is not null)
            _inbox.Add(recebida.Mensagem.MessageId);
        Outbox.AddRange(eventos);
        Gravacoes++;
        return Task.FromResult(true);
    }
}
