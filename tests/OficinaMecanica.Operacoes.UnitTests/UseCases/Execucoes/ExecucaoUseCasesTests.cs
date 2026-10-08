using Moq;
using OficinaMecanica.Operacoes.Application.Contratos.Saga;
using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.In;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Application.UseCases.Catalogo;
using OficinaMecanica.Operacoes.Application.UseCases.Execucoes;
using OficinaMecanica.Operacoes.Application.UseCases.Mensageria;
using OficinaMecanica.Operacoes.Domain.Catalogo;
using OficinaMecanica.Operacoes.Domain.Estoque;
using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.UnitTests.UseCases.Execucoes;

public class ExecucaoUseCasesTests
{
    private const string Mecanico = "mecanico@oficina.example";

    private readonly ExecucaoRepositoryEmMemoria _repo = new();
    private readonly Mock<IEstoqueParaExecucao> _estoque = new();
    private readonly Mock<IPecaRepository> _pecas = new();
    private readonly Mock<IServicoRepository> _servicos = new();

    private readonly Guid _filialId = Guid.NewGuid();
    private readonly Guid _osId = Guid.NewGuid();
    private readonly Guid _correlationId = Guid.NewGuid();
    private readonly Peca _oleo = new("OLEO-5W30-1L", "Óleo 5W30", "", 39.90m);
    private readonly Servico _troca = new("Troca de óleo", "", 80m, 40);

    public ExecucaoUseCasesTests()
    {
        _estoque.Setup(e => e.FilialOperaEstoqueAsync(_filialId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _pecas.Setup(r => r.ObterPorIdAsync(_oleo.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_oleo);
        _servicos.Setup(r => r.ObterPorIdAsync(_troca.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_troca);
    }

    private ComandosExecucaoUseCase Comandos => new(_repo, _estoque.Object);
    private AcoesExecucaoUseCase Acoes => new(_repo, new PrecificarDiagnosticoUseCase(_pecas.Object, _servicos.Object), _estoque.Object);
    private ConsultarExecucoesUseCase Consultas => new(_repo);

    private DiagnosisRequested PedidoDiagnostico(Guid? filialId = null) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = "DiagnosisRequested.v1", SchemaVersion = 1, Producer = "os",
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = _correlationId, CausationId = null, OsId = _osId,
        FilialId = filialId ?? _filialId, IdempotencyKey = $"diagnostico:{_osId}", VeiculoId = Guid.NewGuid()
    };

    private ExecutionStartRequested PedidoInicio(Guid executionId, Guid reservaId, string? chave = null) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = "ExecutionStartRequested.v1", SchemaVersion = 1, Producer = "os",
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = _correlationId, CausationId = Guid.NewGuid(), OsId = _osId,
        FilialId = _filialId, IdempotencyKey = chave ?? $"inicio:{_osId}", ExecutionId = executionId, ReservationId = reservaId
    };

    private static MensagemRecebida Recebida(MensagemSaga m) => new(m, "canal", "{}");

    private async Task<Guid> DiagnosticadaAsync()
    {
        await Comandos.ProcessarAsync(Recebida(PedidoDiagnostico()));
        var id = (await Consultas.ObterPorOsAsync(_osId)).Id;
        await Acoes.RegistrarDiagnosticoAsync(id, Diagnostico(), Mecanico);
        return id;
    }

    private async Task<(Guid Id, Guid ReservaId, ExecutionStartRequested Pedido)> EmReparoAsync()
    {
        var id = await DiagnosticadaAsync();
        var reserva = Guid.NewGuid();
        _estoque.Setup(e => e.ReservaAtivaDaOsAsync(reserva, _osId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var pedido = PedidoInicio(id, reserva);
        await Comandos.ProcessarAsync(Recebida(pedido));
        await Acoes.IniciarReparoAsync(id, Mecanico);
        return (id, reserva, pedido);
    }

    private RegistrarDiagnosticoRequest Diagnostico() => new()
    {
        Itens =
        [
            new() { Tipo = TipoItemDiagnostico.Peca, ItemId = _oleo.Id, Quantidade = 4 },
            new() { Tipo = TipoItemDiagnostico.Servico, ItemId = _troca.Id, Quantidade = 1 }
        ]
    };

    // ── Comandos da Saga ──────────────────────────────────────────────────────

    [Fact]
    public async Task DiagnosisRequested_AbreExecucaoSemEvento()
    {
        var status = await Comandos.ProcessarAsync(Recebida(PedidoDiagnostico()));

        Assert.Equal(StatusProcessamento.Processada, status);
        Assert.Equal(StatusExecucao.EmDiagnostico, (await Consultas.ObterPorOsAsync(_osId)).Status);
        Assert.Empty(_repo.Outbox);
    }

    [Fact]
    public async Task DiagnosisRequested_FilialSemOperacao_RejeitaComEvento()
    {
        var pedido = PedidoDiagnostico(Guid.NewGuid());

        await Comandos.ProcessarAsync(Recebida(pedido));

        var rejeicao = Assert.IsType<DiagnosisRejected>(Assert.Single(_repo.Outbox));
        Assert.Equal(pedido.MessageId, rejeicao.CausationId);
        Assert.Equal(StatusExecucao.DiagnosticoRejeitado, (await Consultas.ObterPorOsAsync(_osId)).Status);
    }

    [Fact]
    public async Task DiagnosisRequested_RepetidoOuOutraMensagemDaMesmaOs_UmaExecucao()
    {
        var pedido = PedidoDiagnostico();

        var primeira = await Comandos.ProcessarAsync(Recebida(pedido));
        var mesmaMensagem = await Comandos.ProcessarAsync(Recebida(pedido));
        var outraMensagem = await Comandos.ProcessarAsync(Recebida(PedidoDiagnostico()));

        Assert.Equal(StatusProcessamento.Processada, primeira);
        Assert.Equal(StatusProcessamento.Duplicada, mesmaMensagem);
        Assert.Equal(StatusProcessamento.Processada, outraMensagem);
        Assert.Single(await Consultas.ListarFilaAsync(_filialId, StatusExecucao.EmDiagnostico));
    }

    [Fact]
    public async Task ExecutionStartRequested_ComReservaAtiva_EnfileiraEPublicaExecutionStarted()
    {
        var id = await DiagnosticadaAsync();
        var reserva = Guid.NewGuid();
        _estoque.Setup(e => e.ReservaAtivaDaOsAsync(reserva, _osId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var pedido = PedidoInicio(id, reserva);

        await Comandos.ProcessarAsync(Recebida(pedido));

        var iniciada = Assert.IsType<ExecutionStarted>(_repo.Outbox.Last());
        Assert.Equal(pedido.MessageId, iniciada.CausationId);
        var execucao = await Consultas.ObterAsync(id);
        Assert.Equal(StatusExecucao.NaFila, execucao.Status);
        Assert.Equal(reserva, execucao.ReservaId);
    }

    [Fact]
    public async Task ExecutionStartRequested_AntesDoDiagnostico_Recusa()
    {
        await Comandos.ProcessarAsync(Recebida(PedidoDiagnostico()));
        var id = (await Consultas.ObterPorOsAsync(_osId)).Id;

        await Comandos.ProcessarAsync(Recebida(PedidoInicio(id, Guid.NewGuid())));

        var recusa = Assert.IsType<ExecutionStartRejected>(_repo.Outbox.Last());
        Assert.Contains("EmDiagnostico", recusa.Reason);
        Assert.Equal(StatusExecucao.EmDiagnostico, (await Consultas.ObterAsync(id)).Status);
    }

    [Fact]
    public async Task ExecutionStartRequested_SemReservaAtivaOuExecucaoInexistente_Recusa()
    {
        var id = await DiagnosticadaAsync();

        await Comandos.ProcessarAsync(Recebida(PedidoInicio(id, Guid.NewGuid())));
        await Comandos.ProcessarAsync(Recebida(PedidoInicio(Guid.NewGuid(), Guid.NewGuid())));

        var recusas = _repo.Outbox.OfType<ExecutionStartRejected>().ToList();
        Assert.Equal(2, recusas.Count);
        Assert.Contains("Reserva", recusas[0].Reason);
        Assert.Contains("não encontrada", recusas[1].Reason);
        Assert.Equal(StatusExecucao.Diagnosticada, (await Consultas.ObterAsync(id)).Status);
    }

    [Fact]
    public async Task ExecutionStartRequested_MesmaChaveEmOutraMensagem_SemNovoEvento()
    {
        var (id, reserva, pedido) = await EmReparoAsync();
        var eventos = _repo.Outbox.Count;

        var status = await Comandos.ProcessarAsync(Recebida(PedidoInicio(id, reserva, pedido.IdempotencyKey)));

        Assert.Equal(StatusProcessamento.Processada, status);
        Assert.Equal(eventos, _repo.Outbox.Count);
    }

    // ── Ações do técnico ──────────────────────────────────────────────────────

    [Fact]
    public async Task Diagnostico_PrecificaPeloCatalogoEPublicaSnapshot()
    {
        await DiagnosticadaAsync();

        var diagnostico = Assert.IsType<DiagnosisCompleted>(Assert.Single(_repo.Outbox));
        Assert.Equal("BRL", diagnostico.Currency);
        Assert.Contains(diagnostico.Items, i => i.Type == "Peca" && i.UnitPrice == 39.90m && i.Quantity == 4 && i.Description == "Óleo 5W30");
        Assert.Contains(diagnostico.Items, i => i.Type == "Servico" && i.UnitPrice == 80m);
        Assert.Equal(_correlationId, diagnostico.CorrelationId);
    }

    [Fact]
    public async Task Diagnostico_ItemInativoOuInexistente_NaoGrava()
    {
        await Comandos.ProcessarAsync(Recebida(PedidoDiagnostico()));
        var id = (await Consultas.ObterPorOsAsync(_osId)).Id;
        var gravacoes = _repo.Gravacoes;

        _oleo.Desativar();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Acoes.RegistrarDiagnosticoAsync(id, Diagnostico(), Mecanico));
        await Assert.ThrowsAsync<NotFoundException>(() => Acoes.RegistrarDiagnosticoAsync(id, new RegistrarDiagnosticoRequest
        {
            Itens = [new() { Tipo = TipoItemDiagnostico.Servico, ItemId = Guid.NewGuid(), Quantidade = 1 }]
        }, Mecanico));

        Assert.Equal(gravacoes, _repo.Gravacoes);
    }

    [Fact]
    public async Task RejeitarDiagnostico_PublicaDiagnosisRejected()
    {
        await Comandos.ProcessarAsync(Recebida(PedidoDiagnostico()));
        var id = (await Consultas.ObterPorOsAsync(_osId)).Id;

        await Acoes.RejeitarDiagnosticoAsync(id, "Veículo sem conserto viável", Mecanico);

        Assert.Equal("Veículo sem conserto viável", Assert.IsType<DiagnosisRejected>(Assert.Single(_repo.Outbox)).Reason);
    }

    [Fact]
    public async Task ProgressoDoReparo_SemEventoParaOOs()
    {
        var (id, _, _) = await EmReparoAsync();
        var eventos = _repo.Outbox.Count;

        var resposta = await Acoes.RegistrarEtapaAsync(id, "Óleo drenado", Mecanico);

        Assert.Equal(eventos, _repo.Outbox.Count);
        Assert.Single(resposta.Etapas);
    }

    [Fact]
    public async Task Conclusao_ConsomeReservaAntesEPublicaExecutionCompleted()
    {
        var (id, reserva, pedidoInicio) = await EmReparoAsync();

        await Acoes.ConcluirAsync(id, [new() { PecaId = _oleo.Id, Quantidade = 3 }], Mecanico);

        _estoque.Verify(e => e.ConcluirConsumoAsync(reserva,
            It.Is<IReadOnlyList<ItemQuantidade>>(l => l.Single().Quantidade == 3), It.IsAny<CancellationToken>()), Times.Once);
        var concluida = Assert.IsType<ExecutionCompleted>(_repo.Outbox.Last());
        Assert.Equal(pedidoInicio.MessageId, concluida.CausationId);
        Assert.Equal(3, Assert.Single(concluida.ConsumedItems).Quantity);
    }

    [Fact]
    public async Task Conclusao_FalhaAoGravarNoDynamo_RepeticaoConcluiSemPerderOEvento()
    {
        // O consumo no PostgreSQL é idempotente (CARD-38a); a repetição grava o agregado e o evento.
        var (id, _, _) = await EmReparoAsync();
        _repo.FalhaNaProximaGravacao = new TimeoutException("DynamoDB não respondeu");

        await Assert.ThrowsAsync<TimeoutException>(() => Acoes.ConcluirAsync(id, [new() { PecaId = _oleo.Id, Quantidade = 4 }], Mecanico));
        Assert.Equal(StatusExecucao.EmReparo, (await Consultas.ObterAsync(id)).Status);

        await Acoes.ConcluirAsync(id, [new() { PecaId = _oleo.Id, Quantidade = 4 }], Mecanico);

        Assert.Equal(StatusExecucao.Concluida, (await Consultas.ObterAsync(id)).Status);
        Assert.Single(_repo.Outbox.OfType<ExecutionCompleted>());
        _estoque.Verify(e => e.ConcluirConsumoAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ItemQuantidade>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Conclusao_ConsumoInvalido_NaoTocaNoEstoque()
    {
        var (id, _, _) = await EmReparoAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Acoes.ConcluirAsync(id, [new() { PecaId = _oleo.Id, Quantidade = 5 }], Mecanico));

        _estoque.Verify(e => e.ConcluirConsumoAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ItemQuantidade>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Falha_RegistraConsumoEPublicaExecutionFailed()
    {
        var (id, reserva, _) = await EmReparoAsync();

        await Acoes.RegistrarFalhaAsync(id, "Peça danificada na instalação", [new() { PecaId = _oleo.Id, Quantidade = 1 }], Mecanico);

        _estoque.Verify(e => e.RegistrarConsumoComFalhaAsync(reserva, It.IsAny<IReadOnlyList<ItemQuantidade>>(), It.IsAny<CancellationToken>()), Times.Once);
        var falha = Assert.IsType<ExecutionFailed>(_repo.Outbox.Last());
        Assert.Equal("Peça danificada na instalação", falha.Reason);
        Assert.Equal(StatusExecucao.Falhou, (await Consultas.ObterAsync(id)).Status);
    }

    [Fact]
    public async Task GravacaoConcorrente_ConflitoNaSegunda()
    {
        await Comandos.ProcessarAsync(Recebida(PedidoDiagnostico()));
        var id = (await Consultas.ObterPorOsAsync(_osId)).Id;
        var primeira = (await _repo.ObterAsync(id))!;
        var segunda = (await _repo.ObterAsync(id))!;

        primeira.RejeitarDiagnostico("Motivo A", Mecanico);
        await _repo.GravarAsync(primeira, []);
        segunda.RejeitarDiagnostico("Motivo B", Mecanico);

        await Assert.ThrowsAsync<ConflitoConcorrenciaException>(() => _repo.GravarAsync(segunda, []));
    }

    [Fact]
    public async Task Consultas_InexistenteNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Consultas.ObterAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<NotFoundException>(() => Consultas.ObterPorOsAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<NotFoundException>(() => Acoes.IniciarReparoAsync(Guid.NewGuid(), Mecanico));
    }
}
