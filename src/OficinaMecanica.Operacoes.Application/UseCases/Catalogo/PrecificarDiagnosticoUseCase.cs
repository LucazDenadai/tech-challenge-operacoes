using OficinaMecanica.Operacoes.Application.Exceptions;
using OficinaMecanica.Operacoes.Application.Ports.In;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Domain.Execucoes;

namespace OficinaMecanica.Operacoes.Application.UseCases.Catalogo;

public class PrecificarDiagnosticoUseCase(IPecaRepository pecaRepository, IServicoRepository servicoRepository) : ICatalogoParaExecucao
{
    public async Task<IReadOnlyList<ItemDiagnostico>> PrecificarAsync(IReadOnlyList<ItemSolicitado> itens, CancellationToken ct = default)
    {
        var resultado = new List<ItemDiagnostico>();
        foreach (var item in itens)
        {
            var (nome, preco, ativo) = item.Tipo switch
            {
                TipoItemDiagnostico.Peca => await pecaRepository.ObterPorIdAsync(item.ItemId, ct) is { } p
                    ? (p.Nome, p.PrecoTabela, p.Ativo)
                    : throw new NotFoundException("Peça", item.ItemId),
                TipoItemDiagnostico.Servico => await servicoRepository.ObterPorIdAsync(item.ItemId, ct) is { } s
                    ? (s.Nome, s.Preco, s.Ativo)
                    : throw new NotFoundException("Serviço", item.ItemId),
                _ => throw new ArgumentException($"Tipo de item '{item.Tipo}' desconhecido.")
            };

            if (!ativo)
                throw new InvalidOperationException($"O item '{nome}' está inativo no catálogo.");

            resultado.Add(new ItemDiagnostico(item.ItemId, item.Tipo, nome, item.Quantidade, preco));
        }
        return resultado;
    }
}
