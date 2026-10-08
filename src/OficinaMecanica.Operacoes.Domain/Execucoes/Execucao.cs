namespace OficinaMecanica.Operacoes.Domain.Execucoes;

// Agregado da execução de uma OS, persistido no DynamoDB (ADR-016). Uma execução por OS.
// O Id é o executionId dos contratos. Versao é o token de concorrência otimista da gravação.
public class Execucao
{
    public const string Sistema = "operacoes";

    private readonly List<ItemDiagnostico> _itens = new();
    private readonly List<EtapaReparo> _etapas = new();
    private readonly List<TransicaoExecucao> _historico = new();
    private readonly List<PecaConsumida> _consumidas = new();

    public Guid Id { get; private set; }
    public Guid OsId { get; private set; }
    public Guid FilialId { get; private set; }
    public Guid VeiculoId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public StatusExecucao Status { get; private set; }
    public int Versao { get; private set; }
    public DateTime CriadaEm { get; private set; }
    public DateTime AtualizadaEm { get; private set; }

    // Comandos da Saga que originaram cada fase: viram causationId dos eventos publicados depois.
    public Guid ComandoDiagnosticoId { get; private set; }
    public Guid? ComandoInicioId { get; private set; }
    public string? ChaveInicio { get; private set; }

    public DateTime? DiagnosticadaEm { get; private set; }
    public Guid? ReservaId { get; private set; }
    public DateTime? EnfileiradaEm { get; private set; }
    public DateTime? EncerradaEm { get; private set; }
    public string? Motivo { get; private set; }

    public IReadOnlyList<ItemDiagnostico> Itens => _itens;
    public IReadOnlyList<EtapaReparo> Etapas => _etapas;
    public IReadOnlyList<TransicaoExecucao> Historico => _historico;
    public IReadOnlyList<PecaConsumida> Consumidas => _consumidas;

    private Execucao() { }

    // Criada pelo DiagnosisRequested.
    public static Execucao Abrir(Guid osId, Guid filialId, Guid veiculoId, Guid correlationId, Guid comandoDiagnosticoId)
    {
        if (osId == Guid.Empty)
            throw new ArgumentException("A OS da execução é obrigatória.", nameof(osId));
        if (filialId == Guid.Empty)
            throw new ArgumentException("A filial da execução é obrigatória.", nameof(filialId));

        var agora = DateTime.UtcNow;
        var execucao = new Execucao
        {
            Id = Guid.NewGuid(),
            OsId = osId,
            FilialId = filialId,
            VeiculoId = veiculoId,
            CorrelationId = correlationId,
            ComandoDiagnosticoId = comandoDiagnosticoId,
            Status = StatusExecucao.EmDiagnostico,
            CriadaEm = agora,
            AtualizadaEm = agora
        };
        execucao._historico.Add(new TransicaoExecucao(null, StatusExecucao.EmDiagnostico, agora, Sistema, null));
        return execucao;
    }

    public void RegistrarDiagnostico(IReadOnlyList<ItemDiagnostico> itens, string responsavel)
    {
        ExigirStatus(StatusExecucao.EmDiagnostico);
        if (itens.Count == 0)
            throw new ArgumentException("O diagnóstico precisa de ao menos um item.", nameof(itens));
        foreach (var item in itens)
            item.Validar();
        if (itens.GroupBy(i => (i.Tipo, i.ItemId)).Any(g => g.Count() > 1))
            throw new ArgumentException("O mesmo item aparece mais de uma vez no diagnóstico.", nameof(itens));

        _itens.AddRange(itens);
        DiagnosticadaEm = DateTime.UtcNow;
        Mudar(StatusExecucao.Diagnosticada, responsavel, null);
    }

    public void RejeitarDiagnostico(string motivo, string responsavel)
    {
        ExigirStatus(StatusExecucao.EmDiagnostico);
        Encerrar(StatusExecucao.DiagnosticoRejeitado, ExigirMotivo(motivo), responsavel);
    }

    // ExecutionStartRequested aceito: a execução diagnosticada entra na fila (ADR-017).
    public void Enfileirar(Guid reservaId, Guid comandoInicioId, string chaveInicio)
    {
        ExigirStatus(StatusExecucao.Diagnosticada);
        if (reservaId == Guid.Empty)
            throw new ArgumentException("A reserva é obrigatória para iniciar a execução.", nameof(reservaId));

        ReservaId = reservaId;
        ComandoInicioId = comandoInicioId;
        ChaveInicio = chaveInicio;
        EnfileiradaEm = DateTime.UtcNow;
        Mudar(StatusExecucao.NaFila, Sistema, null);
    }

    public void IniciarReparo(string responsavel)
    {
        ExigirStatus(StatusExecucao.NaFila);
        Mudar(StatusExecucao.EmReparo, responsavel, null);
    }

    // Progresso intermediário: fica no agregado e na API, sem evento para o OS (emenda do ADR-015).
    public void RegistrarEtapa(string descricao, string responsavel)
    {
        ExigirStatus(StatusExecucao.EmReparo);
        if (string.IsNullOrWhiteSpace(descricao))
            throw new ArgumentException("A descrição da etapa é obrigatória.", nameof(descricao));

        _etapas.Add(new EtapaReparo(descricao.Trim(), ExigirResponsavel(responsavel), DateTime.UtcNow));
        AtualizadaEm = DateTime.UtcNow;
    }

    // Confere a conclusão sem alterar nada: o consumo no estoque acontece antes de gravar o agregado.
    public void ValidarConclusao(IReadOnlyList<PecaConsumida> consumidas)
    {
        ExigirStatus(StatusExecucao.EmReparo);
        ValidarConsumo(consumidas);
    }

    public void Concluir(IReadOnlyList<PecaConsumida> consumidas, string responsavel)
    {
        ValidarConclusao(consumidas);
        _consumidas.AddRange(consumidas.Where(c => c.Quantidade > 0));
        Encerrar(StatusExecucao.Concluida, null, responsavel);
    }

    // A falha pode acontecer na fila ou durante o reparo; informa o consumo real (ADR-017).
    public void ValidarFalha(string motivo, IReadOnlyList<PecaConsumida> consumidas)
    {
        if (Status is not (StatusExecucao.NaFila or StatusExecucao.EmReparo))
            throw new InvalidOperationException($"Execução no status '{Status}' não pode registrar falha.");
        ExigirMotivo(motivo);
        ValidarConsumo(consumidas);
    }

    public void Falhar(string motivo, IReadOnlyList<PecaConsumida> consumidas, string responsavel)
    {
        ValidarFalha(motivo, consumidas);
        _consumidas.AddRange(consumidas.Where(c => c.Quantidade > 0));
        Encerrar(StatusExecucao.Falhou, motivo.Trim(), responsavel);
    }

    // Só o repositório marca a versão gravada.
    public void MarcarGravada(int versao) => Versao = versao;

    private void ValidarConsumo(IReadOnlyList<PecaConsumida> consumidas)
    {
        var pecas = _itens.Where(i => i.Tipo == TipoItemDiagnostico.Peca).ToDictionary(i => i.ItemId, i => i.Quantidade);
        foreach (var grupo in consumidas.GroupBy(c => c.PecaId))
        {
            var total = grupo.Sum(c => c.Quantidade);
            if (grupo.Any(c => c.Quantidade < 0))
                throw new ArgumentException($"Quantidade consumida negativa para a peça '{grupo.Key}'.");
            if (!pecas.TryGetValue(grupo.Key, out var diagnosticada))
                throw new InvalidOperationException($"A peça '{grupo.Key}' não faz parte do diagnóstico.");
            if (total > diagnosticada)
                throw new InvalidOperationException($"Consumo da peça '{grupo.Key}' maior que o diagnosticado.");
        }
    }

    private void Encerrar(StatusExecucao status, string? motivo, string responsavel)
    {
        Motivo = motivo;
        EncerradaEm = DateTime.UtcNow;
        Mudar(status, responsavel, motivo);
    }

    private void Mudar(StatusExecucao para, string responsavel, string? motivo)
    {
        var agora = DateTime.UtcNow;
        _historico.Add(new TransicaoExecucao(Status, para, agora, ExigirResponsavel(responsavel), motivo));
        Status = para;
        AtualizadaEm = agora;
    }

    private void ExigirStatus(StatusExecucao esperado)
    {
        if (Status != esperado)
            throw new InvalidOperationException($"Execução no status '{Status}' não aceita esta operação (esperado '{esperado}').");
    }

    private static string ExigirMotivo(string motivo)
        => string.IsNullOrWhiteSpace(motivo) ? throw new ArgumentException("O motivo é obrigatório.", nameof(motivo)) : motivo.Trim();

    private static string ExigirResponsavel(string responsavel)
        => string.IsNullOrWhiteSpace(responsavel) ? throw new ArgumentException("O responsável é obrigatório.", nameof(responsavel)) : responsavel;

    // Reconstrói o agregado lido do banco, sem passar pelas regras de transição.
    public static Execucao Reidratar(EstadoExecucao e)
    {
        var execucao = new Execucao
        {
            Id = e.Id, OsId = e.OsId, FilialId = e.FilialId, VeiculoId = e.VeiculoId, CorrelationId = e.CorrelationId,
            Status = e.Status, Versao = e.Versao, CriadaEm = e.CriadaEm, AtualizadaEm = e.AtualizadaEm,
            ComandoDiagnosticoId = e.ComandoDiagnosticoId, ComandoInicioId = e.ComandoInicioId, ChaveInicio = e.ChaveInicio,
            DiagnosticadaEm = e.DiagnosticadaEm, ReservaId = e.ReservaId, EnfileiradaEm = e.EnfileiradaEm,
            EncerradaEm = e.EncerradaEm, Motivo = e.Motivo
        };
        execucao._itens.AddRange(e.Itens);
        execucao._etapas.AddRange(e.Etapas);
        execucao._historico.AddRange(e.Historico);
        execucao._consumidas.AddRange(e.Consumidas);
        return execucao;
    }

    public EstadoExecucao Estado() => new(
        Id, OsId, FilialId, VeiculoId, CorrelationId, Status, Versao, CriadaEm, AtualizadaEm,
        ComandoDiagnosticoId, ComandoInicioId, ChaveInicio, DiagnosticadaEm, ReservaId, EnfileiradaEm, EncerradaEm, Motivo,
        _itens.ToList(), _etapas.ToList(), _historico.ToList(), _consumidas.ToList());
}

// Fotografia do agregado para persistência. O formato pode evoluir sem migração (ADR-016).
public sealed record EstadoExecucao(
    Guid Id, Guid OsId, Guid FilialId, Guid VeiculoId, Guid CorrelationId, StatusExecucao Status, int Versao,
    DateTime CriadaEm, DateTime AtualizadaEm, Guid ComandoDiagnosticoId, Guid? ComandoInicioId, string? ChaveInicio,
    DateTime? DiagnosticadaEm, Guid? ReservaId, DateTime? EnfileiradaEm, DateTime? EncerradaEm, string? Motivo,
    IReadOnlyList<ItemDiagnostico> Itens, IReadOnlyList<EtapaReparo> Etapas,
    IReadOnlyList<TransicaoExecucao> Historico, IReadOnlyList<PecaConsumida> Consumidas);
