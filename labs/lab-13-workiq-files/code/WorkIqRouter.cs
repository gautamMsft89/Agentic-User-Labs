namespace WorkIqFiles;

internal sealed record WorkIqRoute(bool Human, string Binding)
{
    internal string Label => Human ? "WorkIQ identity: requesting user" : "WorkIQ identity: AU";
}
internal sealed class RouteLease(IWorkIq backend, WorkIqRoute route, HumanLease? human) : IAsyncDisposable
{
    internal long? HumanGeneration => human?.Generation;
    internal IWorkIq Backend => backend;
    internal WorkIqRoute Route => route;
    public ValueTask DisposeAsync() => human?.DisposeAsync() ?? ValueTask.CompletedTask;
}
internal sealed class WorkIqRouter(WorkIqSession au, HumanConnections? humans, HumanSettings? humanSettings,
    LabSettings teams, string setupMessage = "Human OAuth is disabled or unconfigured. Configure HumanWorkIQ before personal-drive sign-in; AU mode remains available.",
    WorkIqHostPolicy? hostPolicy = null)
{
    internal bool HumanIdentityAllowed => hostPolicy?.HumanOAuthEnabled ?? true;
    internal bool PrivateJobsAllowed => hostPolicy?.PrivateJobsEnabled ?? true;
    internal string HostStatus => hostPolicy?.Status ?? "";
    internal string IdentityGuidance => hostPolicy?.IdentityGuidance ?? "";
    internal Task<string> SignIn(Invocation invocation, CancellationToken ct) =>
        HumanIdentityAllowed ? humans?.CreateLink(invocation, ct) ?? Task.FromResult(setupMessage) :
            Task.FromResult(hostPolicy?.AllowHumanIdentity == false ? WorkIqHostPolicy.HumanUnavailable : setupMessage);
    internal Task<string> HumanStatus(Invocation invocation, CancellationToken ct) =>
        HumanIdentityAllowed ? humans?.Status(invocation, ct) ?? Task.FromResult(setupMessage) : Task.FromResult(setupMessage);
    internal IWorkIq NaturalLanguageAu(Invocation invocation)
    {
        if (invocation.Type is not ("personal" or "channel") || invocation.Policy.GroupShareChannelId.Length != 0 ||
            invocation.Policy.SharingRosterDiscoveryOnly)
            throw new LabException("Natural-language mode is not available in sharing/group contexts.");
        return au;
    }
    internal async Task<RouteLease> AcquireDirect(Invocation invocation, string principal, CancellationToken ct)
    {
        _ = NaturalLanguageAu(invocation);
        if (principal == "AgentUser")
            return new(au, new(false, $"direct-au|{teams.TenantId}|{teams.AgentUserObjectId}|{teams.AgentIdentityClientId}|{teams.BlueprintClientId}"), null);
        if (principal != "SignedInHuman" || invocation.Type != "personal")
            throw new LabException("Direct human profile requires the matched requester in an approved personal chat; no AU fallback.");
        if (!HumanIdentityAllowed) throw new LabException(WorkIqHostPolicy.HumanUnavailable);
        if (humans is null) throw new HumanSignInRequiredException();
        HumanLease lease = await humans.Acquire(invocation, ct);
        return new(lease.Backend, new(true,
            $"direct-human|{invocation.Tenant}|{invocation.Requester}|{humanSettings?.Client}|generation={lease.Generation}"), lease);
    }
    internal Task<string> Disconnect(Invocation invocation) =>
        HumanIdentityAllowed ? humans?.Disconnect(invocation) ?? Task.FromResult("No human connection is configured. AU mode and Teams messaging are unchanged.") :
            Task.FromResult("Human identity is inactive. Stored human credentials/jobs were not accessed or deleted. " + HostStatus);
}
