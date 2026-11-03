using System.Text;
using OficinaMecanica.Operacoes.Application.Contratos.Saga;
using OficinaMecanica.Operacoes.Application.Validators;

namespace OficinaMecanica.Operacoes.UnitTests.Contratos;

// Caminhos de recusa do leitor e do envelope que os exemplos da spec não exercitam.
public class LeitorEEnvelopeTests
{
    private static readonly CanalConsumido _canal = CatalogoCanaisOperacoes.Consumidos[0];
    private static readonly PecaQuantity[] _pecas = [new() { PecaId = Guid.NewGuid(), Quantity = 2 }];

    private static MensagemSaga Comando()
        => LeitorMensagemSaga.Ler(_canal, MensagensExemplo.Bytes(MensagensExemplo.Criar(_canal))).Mensagem!;

    [Fact]
    public void Ler_CorpoQueNaoEJson_RejeitadoSemIdentificadores()
    {
        var leitura = LeitorMensagemSaga.Ler(_canal, Encoding.UTF8.GetBytes("{ isto não é json"));

        Assert.False(leitura.Valida);
        Assert.Equal("Corpo não é um JSON válido.", leitura.Motivo);
        Assert.Null(leitura.MessageId);
        Assert.Null(leitura.CorrelationId);
    }

    [Fact]
    public void Ler_JsonQueNaoEObjeto_Rejeitado()
    {
        var leitura = LeitorMensagemSaga.Ler(_canal, Encoding.UTF8.GetBytes("[1, 2]"));

        Assert.False(leitura.Valida);
        Assert.Equal("Corpo não é um objeto JSON.", leitura.Motivo);
    }

    [Fact]
    public void Envelope_SchemaVersionZeroEProdutorDesconhecido_DoisErros()
    {
        var evento = RespostaSaga.Reservado(Comando(), Guid.NewGuid(), _pecas) with { SchemaVersion = 0, Producer = "estoque" };

        var erros = evento.Validar();

        Assert.Contains(erros, e => e.Contains("schemaVersion"));
        Assert.Contains(erros, e => e.Contains("producer 'estoque'"));
    }

    [Fact]
    public void ReservaELiberacao_Validas_SemErros()
    {
        var comando = Comando();

        Assert.Empty(RespostaSaga.Reservado(comando, Guid.NewGuid(), _pecas).Validar());
        Assert.Empty(RespostaSaga.Liberado(comando, Guid.NewGuid(), _pecas).Validar());
    }

    [Fact]
    public void ReservaELiberacao_QuantidadeNegativa_RecusadasComOCampo()
    {
        var comando = Comando();
        PecaQuantity[] negativa = [new() { PecaId = Guid.NewGuid(), Quantity = -1 }];

        Assert.Contains(RespostaSaga.Reservado(comando, Guid.NewGuid(), negativa).Validar(), e => e.Contains("items[0].quantity"));
        Assert.Contains(RespostaSaga.Liberado(comando, Guid.NewGuid(), negativa).Validar(), e => e.Contains("releasedItems[0].quantity"));
    }

    [Fact]
    public void Publicado_TipoQueOperacoesNaoPublica_LancaExcecao()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CatalogoCanaisOperacoes.Publicado(typeof(InventoryReservationRequested)));

        Assert.Contains(nameof(InventoryReservationRequested), ex.Message);
    }

    public static TheoryData<object?, bool> Ids() => new()
    {
        { Guid.NewGuid(), true },
        { Guid.Empty, false },
        { null, false },
        { "3f2504e0-4f89-11d3-9a0c-0305e82c3301", false }
    };

    [Theory]
    [MemberData(nameof(Ids))]
    public void IdObrigatorio_SoAceitaGuidNaoVazio(object? valor, bool valido)
    {
        Assert.Equal(valido, new IdObrigatorioAttribute().IsValid(valor));
    }
}
