using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FhirPathLab_DotNetEngine.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Register FHIRPath services
        services.AddSingleton<SchemaProviderFactory>();
        services.AddSingleton<ExpressionAnalyzer>();
        services.AddSingleton<ExpressionEvaluator>();
        services.AddSingleton<ResultFormatter>();
        services.AddSingleton<FhirPathService>();
    })
    .Build();

host.Run();
