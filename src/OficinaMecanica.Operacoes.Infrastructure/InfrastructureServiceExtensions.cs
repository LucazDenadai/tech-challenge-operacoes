using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Operacoes.Application.Ports.In;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Application.UseCases.Catalogo;
using OficinaMecanica.Operacoes.Application.UseCases.Estoque;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Repositories;

namespace OficinaMecanica.Operacoes.Infrastructure;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Instância PostgreSQL dedicada a Operações (ADR-016); sem fallback para outro banco.
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' não configurada.");

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IUnidadeDeTrabalho, UnidadeDeTrabalho>();
        services.AddScoped<IPecaRepository, PecaRepository>();
        services.AddScoped<IServicoRepository, ServicoRepository>();
        services.AddScoped<IFilialEstoqueRepository, FilialEstoqueRepository>();
        services.AddScoped<ISaldoEstoqueRepository, SaldoEstoqueRepository>();
        services.AddScoped<IReservaRepository, ReservaRepository>();
        services.AddScoped<IMovimentacaoRepository, MovimentacaoRepository>();

        services.AddScoped<GerenciarPecaUseCase>();
        services.AddScoped<GerenciarServicoUseCase>();
        services.AddScoped<ConsultarEstoqueUseCase>();
        services.AddScoped<MovimentarEstoqueUseCase>();
        services.AddScoped<ReservarEstoqueUseCase>();
        services.AddScoped<LiberarReservaUseCase>();
        services.AddScoped<ConsumirReservaUseCase>();
        services.AddScoped<IEstoqueParaExecucao>(sp => sp.GetRequiredService<ConsumirReservaUseCase>());

        return services;
    }
}
