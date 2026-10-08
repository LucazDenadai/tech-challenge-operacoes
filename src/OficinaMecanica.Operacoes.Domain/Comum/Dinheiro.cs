namespace OficinaMecanica.Operacoes.Domain.Comum;

// Regras do schema Money do AsyncAPI: positivo e com no máximo duas casas decimais.
public static class Dinheiro
{
    public const string Moeda = "BRL";

    public static decimal Validar(decimal valor, string parametro)
    {
        if (valor <= 0)
            throw new ArgumentException("O preço deve ser maior que zero.", parametro);
        if (decimal.Round(valor, 2) != valor)
            throw new ArgumentException("O preço deve ter no máximo duas casas decimais.", parametro);
        return valor;
    }
}
