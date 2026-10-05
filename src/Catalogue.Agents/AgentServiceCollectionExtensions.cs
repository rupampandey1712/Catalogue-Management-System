using Catalogue.Agents.FoundryLocal;
using Catalogue.Agents.Review;
using Catalogue.Agents.Runs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Catalogue.Agents;

public static class AgentServiceKeys
{
    /// <summary>Keyed IChatClient: the local model with tool-call parsing, function invocation and telemetry.</summary>
    public const string Model = "catalogue-local-model";
}

public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// The operations assistant and review workflow on a Foundry Local model. Nothing touches the model at startup;
    /// it is discovered, started and loaded on first use, so the rest of the app works without Foundry Local.
    /// </summary>
    public static IServiceCollection AddCatalogueAgents(this IServiceCollection services, IConfiguration config)
    {
        var agent = AgentOptions.From(config);
        var foundry = FoundryLocalOptions.From(config);
        services.AddSingleton(agent);
        services.AddSingleton(foundry);
        services.AddSingleton<IFoundryCli, FoundryCli>();
        services.AddSingleton<FoundryLocalRuntime>();
        services.AddSingleton<FoundryLocalChatClient>();
        services.AddKeyedSingleton<IChatClient>(AgentServiceKeys.Model, (sp, _) =>
            new TextToolCallChatClient(sp.GetRequiredService<FoundryLocalChatClient>(), foundry.IsThinkingModel)
                .AsBuilder()
                .UseFunctionInvocation(sp.GetRequiredService<ILoggerFactory>(), f =>
                {
                    f.MaximumIterationsPerRequest = agent.MaxToolCallsPerRun + 1;
                    f.MaximumConsecutiveErrorsPerRequest = 2;
                    f.IncludeDetailedErrors = false;
                    f.AllowConcurrentInvocation = false; // tools share one DbContext per run
                })
                .UseOpenTelemetry(sp.GetRequiredService<ILoggerFactory>(), CatalogueAssistant.SourceName)
                .Build(sp));
        services.AddSingleton<AgentRunCoordinator>();
        services.AddHostedService<AgentRunWorker>();
        services.AddSingleton(sp => new CatalogueAssistant(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<AgentRunCoordinator>(),
            sp.GetRequiredKeyedService<IChatClient>(AgentServiceKeys.Model), sp.GetRequiredService<FoundryLocalRuntime>(), agent,
            sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new OperationsReviewRunner(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<AgentRunCoordinator>(),
            sp.GetRequiredKeyedService<IChatClient>(AgentServiceKeys.Model), sp.GetRequiredService<FoundryLocalRuntime>(), agent,
            sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService<OperationsReviewScheduler>();
        return services;
    }
}
