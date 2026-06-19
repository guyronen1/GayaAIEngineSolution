using AIEngineAPI.Middleware;
using MaiaAI.Application.Classification;
using MaiaAI.Application.Maintenance;
using MaiaAI.Application.Pipeline;
using MaiaAI.Application.Remediation;
using MaiaAI.Application.Security;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;

namespace AIEngineAPI.Extensions;

/// <summary>
/// Composition root for Application-layer use cases.
/// Infrastructure + domain services are wired in Infrastructure.Extensions.ServiceCollectionExtensions.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IClassifyJobsUseCase,         ClassifyJobsUseCase>();
        services.AddScoped<IGenerateSuggestionsUseCase,  GenerateSuggestionsUseCase>();
        services.AddScoped<IExecuteFixesUseCase,         ExecuteFixesUseCase>();
        services.AddScoped<IDirectoryPipelineUseCase,    DirectoryPipelineUseCase>();
        services.AddScoped<IScanHistoryRetentionService, ScanHistoryRetentionService>();
        services.AddScoped<IAuthService,                 AuthService>();

        return services;
    }

    public static IServiceCollection AddGlobalExceptionHandling(this IServiceCollection services)
    {
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();
        return services;
    }
}
