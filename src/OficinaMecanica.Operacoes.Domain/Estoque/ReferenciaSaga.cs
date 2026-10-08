namespace OficinaMecanica.Operacoes.Domain.Estoque;

// Referência de negócio de uma movimentação vinda da Saga, para auditoria pela correlação.
public sealed record ReferenciaSaga(Guid OsId, Guid ReservaId, Guid CorrelationId);
