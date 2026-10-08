using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OficinaMecanica.Operacoes.Application.Contratos.Saga;
using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Application.UseCases.Estoque;
using OficinaMecanica.Operacoes.Application.UseCases.Mensageria;
using OficinaMecanica.Operacoes.UnitTests.Contratos;
using OficinaMecanica.Operacoes.UnitTests.UseCases.Estoque;

namespace OficinaMecanica.Operacoes.UnitTests.UseCases.Mensageria;

public class ProcessarMensagemSagaUseCaseTests
{
    private const string CanalReserva = "saga-os.inventory-reservation-requested.v1";
    private const string CanalLiberacao = "saga-os.inventory-release-requested.v1";

    private readonly EstoqueFixture _f = new();
    private readonly Mock<IInboxRepository> _inbox = new();
    private readonly List<MensagemSaga> _outbox = new();
    private readonly HashSet<Guid> _recebidas = new();

    private ProcessarMensagemSagaUseCase Sut()
    {
        _inbox.Setup(i => i.RegistrarAsync(It.IsAny<MensagemSaga>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MensagemSaga m, string _, string _, CancellationToken _) => _recebidas.Add(m.MessageId));
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.Adicionar(It.IsAny<MensagemSaga>())).Callback((MensagemSaga m) => _outbox.Add(m));

        return new ProcessarMensagemSagaUseCase(new TransacaoDireta(), _inbox.Object, outbox.Object, _f.Uow.Object,
            new ReservarEstoqueUseCase(_f.Filiais.Object, _f.Saldos.Object, _f.Reservas.Object, _f.Movimentacoes.Object),
            new LiberarReservaUseCase(_f.Reservas.Object, _f.Saldos.Object, _f.Movimentacoes.Object),
            NullLogger<ProcessarMensagemSagaUseCase>.Instance);
    }

    private JsonObject PedidoDeReserva(int quantidade)
    {
        var pedido = MensagensExemplo.Criar(CatalogoCanaisOperacoes.ObterConsumido(CanalReserva)!);
        pedido["filialId"] = _f.Filial.Id.ToString();
        pedido["items"] = new JsonArray(new JsonObject { ["pecaId"] = _f.Oleo.Id.ToString(), ["quantity"] = quantidade });
        return pedido;
    }

    [Fact]
    public async Task Reserva_ConfirmadaGravaEPublicaInventoryReservedComCausacao()
    {
        _f.ComSaldo(_f.Oleo, 10);
        var pedido = PedidoDeReserva(4);

        var resultado = await Sut().ExecutarAsync(CanalReserva, MensagensExemplo.Bytes(pedido));

        Assert.Equal(StatusProcessamento.Processada, resultado.Status);
        var evento = Assert.IsType<InventoryReserved>(Assert.Single(_outbox));
        Assert.Equal(pedido["messageId"]!.GetValue<string>(), evento.CausationId.ToString());
        Assert.Equal(pedido["correlationId"]!.GetValue<string>(), evento.CorrelationId.ToString());
        Assert.Equal(4, Assert.Single(evento.Items).Quantity);
        _f.Uow.Verify(u => u.SalvarAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reserva_SemSaldoPublicaRecusaComPecas()
    {
        _f.ComSaldo(_f.Oleo, 1);

        await Sut().ExecutarAsync(CanalReserva, MensagensExemplo.Bytes(PedidoDeReserva(4)));

        var recusa = Assert.IsType<InventoryReservationRejected>(Assert.Single(_outbox));
        Assert.Equal([_f.Oleo.Id], recusa.UnavailablePecaIds!);
        Assert.Empty(_f.ReservasAdicionadas);
    }

    [Fact]
    public async Task MensagemRepetida_DuplicadaSemNovoEfeito()
    {
        _f.ComSaldo(_f.Oleo, 10);
        var corpo = MensagensExemplo.Bytes(PedidoDeReserva(2));
        var sut = Sut();

        await sut.ExecutarAsync(CanalReserva, corpo);
        var segunda = await sut.ExecutarAsync(CanalReserva, corpo);

        Assert.Equal(StatusProcessamento.Duplicada, segunda.Status);
        Assert.Single(_outbox);
        Assert.Equal(2, _f.SaldosDaFilial[_f.Oleo.Id].QuantidadeReservada);
    }

    [Fact]
    public async Task Liberacao_PublicaInventoryReleased()
    {
        _f.ComSaldo(_f.Oleo, 10);
        var sut = Sut();
        await sut.ExecutarAsync(CanalReserva, MensagensExemplo.Bytes(PedidoDeReserva(3)));
        var reservado = (InventoryReserved)_outbox[0];

        var liberacao = MensagensExemplo.Criar(CatalogoCanaisOperacoes.ObterConsumido(CanalLiberacao)!);
        liberacao["reservationId"] = reservado.ReservationId.ToString();
        var resultado = await sut.ExecutarAsync(CanalLiberacao, MensagensExemplo.Bytes(liberacao));

        Assert.Equal(StatusProcessamento.Processada, resultado.Status);
        var liberado = Assert.IsType<InventoryReleased>(_outbox[1]);
        Assert.Equal(3, Assert.Single(liberado.ReleasedItems).Quantity);
        Assert.Equal(10, _f.SaldosDaFilial[_f.Oleo.Id].QuantidadeDisponivel);
    }

    [Fact]
    public async Task Liberacao_DeReservaInexistente_RejeitadaSemPublicar()
    {
        var liberacao = MensagensExemplo.Criar(CatalogoCanaisOperacoes.ObterConsumido(CanalLiberacao)!);

        var resultado = await Sut().ExecutarAsync(CanalLiberacao, MensagensExemplo.Bytes(liberacao));

        Assert.Equal(StatusProcessamento.Rejeitada, resultado.Status);
        Assert.Contains("Reserva", resultado.Motivo);
        Assert.Empty(_outbox);
    }

    [Fact]
    public async Task ForaDoContratoOuCanalDesconhecido_Rejeitada()
    {
        var invalida = PedidoDeReserva(1);
        invalida.Remove("idempotencyKey");
        var sut = Sut();

        var semChave = await sut.ExecutarAsync(CanalReserva, MensagensExemplo.Bytes(invalida));
        var outroCanal = await sut.ExecutarAsync("saga-os.payment-approved.v1", MensagensExemplo.Bytes(invalida));

        Assert.Equal(StatusProcessamento.Rejeitada, semChave.Status);
        Assert.Contains("idempotencyKey", semChave.Motivo);
        Assert.Equal(StatusProcessamento.Rejeitada, outroCanal.Status);
        _inbox.Verify(i => i.RegistrarAsync(It.IsAny<MensagemSaga>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConflitoDeConcorrencia_SobeParaOConsumidorRefazer()
    {
        _f.ComSaldo(_f.Oleo, 10);
        _f.Uow.Setup(u => u.SalvarAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new ConflitoConcorrenciaException(new Exception()));

        await Assert.ThrowsAsync<ConflitoConcorrenciaException>(() =>
            Sut().ExecutarAsync(CanalReserva, MensagensExemplo.Bytes(PedidoDeReserva(1))));
    }

    private sealed class TransacaoDireta : ITransacao
    {
        public Task<T> ExecutarAsync<T>(Func<Task<T>> acao, CancellationToken ct = default) => acao();
    }
}
