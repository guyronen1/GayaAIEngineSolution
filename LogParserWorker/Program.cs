using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared.Interfaces;
using Shared.Implementations;
using Shared.DataAccess;
using Microsoft.EntityFrameworkCore;

Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<Worker>();
        services.AddSingleton<ILogParser, SimpleLogParser>();
        services.AddSingleton<IErrorClassifier, AIRecommendationGenerator>();
        services.AddSingleton<IDataRepository, SqlDataRepository>();
        services.AddDbContext<AiDbContext>(options =>
            options.UseInMemoryDatabase("AiMonitoringDb"));
    })
    .Build()
    .Run();
