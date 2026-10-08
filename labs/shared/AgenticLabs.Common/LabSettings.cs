using Microsoft.Teams.Apps.Schema;

namespace WorkIqFiles;

// Shared identity binding for Teams AU messaging and the independent AU WorkIQ grant.
internal sealed class LabSettings
{
    internal LabSettings(IConfiguration configuration)
    {
        TenantId = RequiredGuid("WorkIQAgent:TenantId");
        AgentIdentityClientId = RequiredGuid("WorkIQAgent:AgentIdentityClientId");
        AgentUserObjectId = RequiredGuid("WorkIQAgent:AgentUserObjectId");
        BlueprintClientId = RequiredGuid("WorkIQAgent:Blueprint:ClientId");
        if (!SameGuid(TenantId, RequiredGuid("WorkIQAgent:Blueprint:TenantId")) ||
            !SameGuid(TenantId, RequiredGuid("AzureAd:TenantId")))
            throw new InvalidOperationException("Teams AU, blueprint and ingress tenants must agree.");
        if (SameGuid(AgentIdentityClientId, BlueprintClientId) || SameGuid(AgentUserObjectId, AgentIdentityClientId) ||
            SameGuid(AgentUserObjectId, BlueprintClientId))
            throw new InvalidOperationException("Teams blueprint, agent identity and AU IDs must be distinct.");

        string RequiredGuid(string key)
        {
            if (!Guid.TryParse(configuration[key], out Guid id) || id == Guid.Empty)
                throw new InvalidOperationException($"{key} must be a nonempty GUID.");
            return id.ToString("D");
        }
    }
    internal string TenantId { get; }
    internal string AgentIdentityClientId { get; }
    internal string AgentUserObjectId { get; }
    internal string BlueprintClientId { get; }
    internal void ValidateInvocationIdentity(MessageActivity activity)
    {
        if (!SameGuid(activity.ChannelData?.Tenant?.Id, TenantId) || !Guid.TryParse(activity.From?.AadObjectId, out _))
            throw new LabException("Denied: trusted command sender and configured tenant are required.");
        var recipient = activity.Recipient;
        if (recipient is null || (recipient.AadObjectId is null && recipient.AgenticUserId is null))
            throw new LabException("Denied: inbound recipient lacks a trusted AU object ID.");
        if ((recipient.AadObjectId is string oid && !SameGuid(oid, AgentUserObjectId)) ||
            (recipient.AgenticUserId is string au && !SameGuid(au, AgentUserObjectId)) ||
            (recipient.AgenticAppId is string app && !SameGuid(app, AgentIdentityClientId)) ||
            (recipient.AgenticAppBlueprintId is string blueprint && !SameGuid(blueprint, BlueprintClientId)) ||
            (recipient.TenantId is string tenant && !SameGuid(tenant, TenantId)))
            throw new LabException("Denied: inbound recipient does not match the configured Teams AU/agent/blueprint.");
    }
    internal static bool SameGuid(string? a, string? b) =>
        Guid.TryParse(a, out Guid left) && Guid.TryParse(b, out Guid right) && left == right;
}
