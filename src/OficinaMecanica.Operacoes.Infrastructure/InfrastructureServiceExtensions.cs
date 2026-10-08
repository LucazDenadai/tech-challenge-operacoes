using Amazon.DynamoDBv2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Messaging;
using OficinaMecanica.Operacoes.Application.Ports.In;
using OficinaMecanica.Operacoes.Application.Ports.Out;
using OficinaMecanica.Operacoes.Application.UseCases.Catalogo;
using OficinaMecanica.Operacoes.Application.UseCases.Estoque;
using OficinaMecanica.Operacoes.Application.UseCases.Execucoes;
using OficinaMecanica.Operacoes.Application.UseCases.Mensageria;
using OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Dynamo;
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
        services.AddScoped<IInboxRepository, InboxRepository>();
        services.AddScoped<IOutbox, OutboxRepository>();
        services.AddScoped<ITransacao, Transacao>();

        services.AddScoped<GerenciarPecaUseCase>();
        services.AddScoped<GerenciarServicoUseCase>();
        services.AddScoped<ConsultarEstoqueUseCase>();
        services.AddScoped<MovimentarEstoqueUseCase>();
        services.AddScoped<ReservarEstoqueUseCase>();
        services.AddScoped<LiberarReservaUseCase>();
        services.AddScoped<ConsumirReservaUseCase>();
        services.AddScoped<IEstoqueParaExecucao>(sp => sp.GetRequiredService<ConsumirReservaUseCase>());
        services.AddScoped<ProcessarMensagemSagaUseCase>();

        // Execução: tabela DynamoDB exclusiva de Operações (ADR-016).
        var secaoDynamo = configuration.GetSection(DynamoDbOptions.Secao);
        var dynamo = secaoDynamo.Get<DynamoDbOptions>() ?? new DynamoDbOptions();
        services.Configure<DynamoDbOptions>(secaoDynamo);
        services.AddSingleton<IAmazonDynamoDB>(_ => dynamo.CriarCliente());
        if (dynamo.CriarTabela)
            services.AddHostedService<CriarTabelaDynamoHostedService>();

        services.AddScoped<IExecucaoRepository, ExecucaoRepository>();
        services.AddScoped<ICatalogoParaExecucao, PrecificarDiagnosticoUseCase>();
        services.AddScoped<ComandosExecucaoUseCase>();
        services.AddScoped<AcoesExecucaoUseCase>();
        services.AddScoped<ConsultarExecucoesUseCase>();

        return services;
    }

    // Consumidor dos comandos da Saga e despachante da outbox (ADR-016, ADR-018). Desabilitado, nenhuma conexão é aberta.
    public static IServiceCollection AddMensageria(this IServiceCollection services, IConfiguration configuration)
    {
        var secao = configuration.GetSection(RabbitMqOptions.Secao);
        var opcoes = secao.Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        if (!opcoes.Enabled)
            return services;

        // Conexão autenticada: sem usuário e senha configurados o serviço não sobe (não usa guest/guest implícito).
        if (string.IsNullOrWhiteSpace(opcoes.Username) || string.IsNullOrWhiteSpace(opcoes.Password))
            throw new InvalidOperationException("RabbitMq:Username e RabbitMq:Password são obrigatórios quando RabbitMq:Enabled=true.");

        services.Configure<RabbitMqOptions>(secao);
        services.AddSingleton<EstadoConsumidorSaga>();
        services.AddHostedService<ConsumidorSagaHostedService>();
        services.AddSingleton<PublicadorRabbitMq>();
        services.AddHostedService<DespachanteOutboxHostedService>();
        services.AddHostedService<DespachanteOutboxDynamoHostedService>();
        return services;
    }
}
