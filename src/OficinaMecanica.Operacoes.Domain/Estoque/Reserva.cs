using System.Diagnostics.CodeAnalysis;
using OficinaMecanica.Operacoes.Domain.Comum;

namespace OficinaMecanica.Operacoes.Domain.Estoque;

// Reserva de peças de uma OS em uma filial (ADR-017). O Id é o reservationId dos contratos.
// Ciclo: Reservar -> Concluir (execução concluída) ou RegistrarFalha + Liberar (compensação).
public class Reserva : EntityBase
{
    public Guid OsId { get; private set; }
    public Guid FilialId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public StatusReserva Status { get; private set; } = StatusReserva.Ativa;
    public bool ConsumoRegistrado { get; private set; }

    private readonly List<ItemReserva> _itens = new();
    public IReadOnlyCollection<ItemReserva> Itens => _itens.AsReadOnly();

    [ExcludeFromCodeCoverage]
    protected Reserva() { }

    private Reserva(Guid osId, Guid filialId, Guid correlationId, string idempotencyKey)
    {
        if (osId == Guid.Empty)
            throw new ArgumentException("A OS da reserva é obrigatória.", nameof(osId));
        if (filialId == Guid.Empty)
            throw new ArgumentException("A filial da reserva é obrigatória.", nameof(filialId));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("A chave de idempotência é obrigatória.", nameof(idempotencyKey));

        OsId = osId;
        FilialId = filialId;
        CorrelationId = correlationId;
        IdempotencyKey = idempotencyKey;
    }

    private ReferenciaSaga Referencia => new(OsId, Id, CorrelationId);

    // Tudo ou nada: confere todos os itens antes de alterar qualquer saldo.
    public static (Reserva Reserva, IReadOnlyList<MovimentacaoEstoque> Movimentacoes) Reservar(
        Guid osId, Guid filialId, Guid correlationId, string idempotencyKey,
        IEnumerable<ItemQuantidade> itens, IReadOnlyDictionary<Guid, SaldoEstoque> saldos)
    {
        var reserva = new Reserva(osId, filialId, correlationId, idempotencyKey);
        var consolidados = ItemQuantidade.Consolidar(itens);

        if (consolidados.Count == 0)
            throw new ArgumentException("A reserva precisa de ao menos um item.", nameof(itens));
        if (consolidados.Any(i => i.Quantidade <= 0))
            throw new ArgumentException("A quantidade reservada deve ser maior que zero.", nameof(itens));

        var indisponiveis = consolidados
            .Where(i => !saldos.TryGetValue(i.PecaId, out var saldo) || saldo.FilialId != filialId || !saldo.PodeReservar(i.Quantidade))
            .Select(i => i.PecaId)
            .ToList();
        if (indisponiveis.Count > 0)
            throw new EstoqueInsuficienteException(indisponiveis);

        var movimentacoes = new List<MovimentacaoEstoque>();
        foreach (var item in consolidados)
        {
            reserva._itens.Add(new ItemReserva(reserva.Id, item.PecaId, item.Quantidade));
            movimentacoes.Add(saldos[item.PecaId].Reservar(item.Quantidade, reserva.Referencia));
        }

        return (reserva, movimentacoes);
    }

    // Execução concluída: consome o usado e devolve o restante ao disponível.
    // Nenhum comando de liberação vem depois, porque a Saga termina em Completed.
    public IReadOnlyList<MovimentacaoEstoque> Concluir(IEnumerable<ItemQuantidade> consumidos, IReadOnlyDictionary<Guid, SaldoEstoque> saldos)
    {
        // Consumo e liberação mexem em itens diferentes: todos os saldos precisam estar carregados antes.
        foreach (var item in _itens)
            Saldo(saldos, item.PecaId);

        var movimentacoes = Consumir(consumidos, saldos).ToList();
        movimentacoes.AddRange(LiberarPendentes("Sobra da reserva após a conclusão da execução", saldos));
        Status = StatusReserva.Consumida;
        MarcarAtualizado();
        return movimentacoes;
    }

    // Execução falhou: consome só o usado. O restante continua reservado até o
    // InventoryReleaseRequested da compensação (ADR-017).
    public IReadOnlyList<MovimentacaoEstoque> RegistrarFalha(IEnumerable<ItemQuantidade> consumidos, IReadOnlyDictionary<Guid, SaldoEstoque> saldos)
    {
        var movimentacoes = Consumir(consumidos, saldos);
        MarcarAtualizado();
        return movimentacoes;
    }

    // Idempotente: liberar uma reserva já liberada ou consumida não altera saldo.
    public IReadOnlyList<MovimentacaoEstoque> Liberar(string motivo, IReadOnlyDictionary<Guid, SaldoEstoque> saldos)
    {
        if (Status != StatusReserva.Ativa)
            return [];

        var movimentacoes = LiberarPendentes(motivo, saldos);
        Status = StatusReserva.Liberada;
        MarcarAtualizado();
        return movimentacoes;
    }

    private IReadOnlyList<MovimentacaoEstoque> Consumir(IEnumerable<ItemQuantidade> consumidos, IReadOnlyDictionary<Guid, SaldoEstoque> saldos)
    {
        if (Status != StatusReserva.Ativa)
            throw new InvalidOperationException($"Reserva no status '{Status}' não aceita consumo.");
        if (ConsumoRegistrado)
            throw new InvalidOperationException("O consumo desta reserva já foi registrado.");

        // Valida tudo e localiza os saldos antes de alterar qualquer item ou saldo.
        var consumos = ItemQuantidade.Consolidar(consumidos)
            .Where(i => i.Quantidade != 0)
            .Select(consumido =>
            {
                var item = _itens.FirstOrDefault(i => i.PecaId == consumido.PecaId)
                    ?? throw new InvalidOperationException($"A peça '{consumido.PecaId}' não faz parte da reserva.");
                if (consumido.Quantidade < 0 || consumido.Quantidade > item.QuantidadePendente)
                    throw new InvalidOperationException($"Consumo da peça '{consumido.PecaId}' fora da quantidade reservada.");
                return (Item: item, Saldo: Saldo(saldos, item.PecaId), consumido.Quantidade);
            })
            .ToList();

        var movimentacoes = new List<MovimentacaoEstoque>();
        foreach (var (item, saldo, quantidade) in consumos)
        {
            item.RegistrarConsumo(quantidade);
            movimentacoes.Add(saldo.Consumir(quantidade, Referencia));
        }

        ConsumoRegistrado = true;
        return movimentacoes;
    }

    private List<MovimentacaoEstoque> LiberarPendentes(string motivo, IReadOnlyDictionary<Guid, SaldoEstoque> saldos)
    {
        var pendentes = _itens
            .Where(i => i.QuantidadePendente > 0)
            .Select(i => (Item: i, Saldo: Saldo(saldos, i.PecaId), Quantidade: i.QuantidadePendente))
            .ToList();

        var movimentacoes = new List<MovimentacaoEstoque>();
        foreach (var (item, saldo, quantidade) in pendentes)
        {
            item.RegistrarLiberacao(quantidade);
            movimentacoes.Add(saldo.Liberar(quantidade, motivo, Referencia));
        }
        return movimentacoes;
    }

    private SaldoEstoque Saldo(IReadOnlyDictionary<Guid, SaldoEstoque> saldos, Guid pecaId)
        => saldos.TryGetValue(pecaId, out var saldo) && saldo.FilialId == FilialId
            ? saldo
            : throw new InvalidOperationException($"Saldo da peça '{pecaId}' na filial '{FilialId}' não foi carregado.");
}
