using Microsoft.Extensions.Configuration;

namespace Catalogue.Agents;

/// <summary>Section <c>Agent</c>: the operations assistant and the scheduled operations review.</summary>
public sealed class AgentOptions
{
    public bool Enabled { get; init; } = true;
    /// <summary>Whole agent run (several model calls and tool calls). Local models are slow; keep this generous.</summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(20);
    /// <summary>Runs waiting for the model. One GPU serves one run at a time.</summary>
    public int MaxQueue { get; init; } = 20;
    /// <summary>Chat messages kept in the model's context (each one costs prompt-processing time on every call).</summary>
    public int HistoryMessages { get; init; } = 8;
    public int MaxToolCallsPerRun { get; init; } = 6;
    public int MaxToolResultChars { get; init; } = 1800;
    public bool ReviewEnabled { get; init; } = true;
    public TimeSpan ReviewInterval { get; init; } = TimeSpan.FromHours(12);
    /// <summary>Ask the model to write the review briefing (otherwise a rule-based briefing is used).</summary>
    public bool ReviewUseModel { get; init; } = true;
    public TimeSpan ReviewModelTimeout { get; init; } = TimeSpan.FromMinutes(12);

    public static AgentOptions From(IConfiguration config)
    {
        var s = config.GetSection("Agent");
        return new AgentOptions
        {
            Enabled = s.GetValue("Enabled", true),
            RunTimeout = s.GetValue("RunTimeout", TimeSpan.FromMinutes(20)),
            MaxQueue = Math.Clamp(s.GetValue("MaxQueue", 20), 1, 500),
            HistoryMessages = Math.Clamp(s.GetValue("HistoryMessages", 8), 2, 50),
            MaxToolCallsPerRun = Math.Clamp(s.GetValue("MaxToolCallsPerRun", 6), 1, 20),
            MaxToolResultChars = Math.Clamp(s.GetValue("MaxToolResultChars", 1800), 300, 20000),
            ReviewEnabled = s.GetValue("Review:Enabled", true),
            ReviewInterval = s.GetValue("Review:Interval", TimeSpan.FromHours(12)),
            ReviewUseModel = s.GetValue("Review:UseModel", true),
            ReviewModelTimeout = s.GetValue("Review:ModelTimeout", TimeSpan.FromMinutes(12)),
        };
    }
}

/// <summary>Policy names shared with the API (the agent only gets tools the user's policies allow).</summary>
public static class AgentPolicies
{
    public const string CatalogueAdmin = "CatalogueAdmin";
    public const string PlatformAdmin = "PlatformAdmin";
    public const string SupplierAdmin = "SupplierAdmin";
    public const string ImportOps = "ImportOps";
    public const string ReadCatalogue = "ReadCatalogue";
    public const string Buyer = "Buyer";
    public const string Seller = "Seller";
    public const string Marketplace = "Marketplace";
    public static readonly string[] All = [CatalogueAdmin, PlatformAdmin, SupplierAdmin, ImportOps, ReadCatalogue, Buyer, Seller, Marketplace];
}

/// <summary>The signed-in user the agent acts for. The agent never has more access than this person.</summary>
public sealed record AgentPrincipal(long UserId, string UserName, string DisplayName, IReadOnlySet<string> Policies, bool IsPlatformStaff,
    bool IsAdmin, long? OrganizationId, string? OrganizationName, long? SupplierId, long? CustomerAccountId)
{
    public bool Can(string policy) => Policies.Contains(policy);

    /// <summary>The scheduled review runs as the platform with read-everything access and no direct write access.</summary>
    public static AgentPrincipal System { get; } = new(0, "operations-review", "Operations review",
        new HashSet<string>(AgentPolicies.All), true, false, null, null, null, null);
}
