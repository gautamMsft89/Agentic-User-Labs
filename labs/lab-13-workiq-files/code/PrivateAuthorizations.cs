using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Teams.Apps.Schema;

namespace WorkIqFiles;

internal sealed record PrivateGrant(string Job, int Revision, string TaskHash, string PersonalBinding,
    PrivateConsentScope ConsentScope = PrivateConsentScope.LegacyReadOnly);

internal sealed class PrivateAuthorizations(PrivateJobStore store, PrivateAnalysisOptions options,
    FilePolicy policy, LabSettings teams, HumanSettings human, NaturalLanguageOptions model, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new();
    internal string Configuration => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        Version = 1, PrivateEnabled = options.Enabled, policy, teams.TenantId, teams.AgentUserObjectId, teams.AgentIdentityClientId,
        teams.BlueprintClientId, human.Client, human.PublicBaseUrl, HumanSettings.Scopes, WorkIqEndpoint = WorkIqSession.Endpoint,
        ModelEnabled = model.Enabled, model.DirectMcp,
        model.Endpoint, model.Deployment, model.ReasoningEffort, model.Budgets
    }))));
    internal void Check(PrivateJob job, bool personal = true)
    {
        if (!options.Enabled || !model.Enabled || !model.DirectMcp.Enabled || job.Configuration != Configuration ||
            job.Terminal || job.Expires <= clock.GetUtcNow())
            throw new LabException("Private request no longer authorized, expired, or terminal.");
        MessageActivity origin = PrivateContext.Activity(job.Origin);
        ObserveReference(PrivatePolicyReference.OriginalChannel, origin, job);
        PrivateContext.RequireSender(origin, teams);
        Invocation original = policy.Authorize(origin, teams);
        if (original.Type != "channel" || original.Requester != job.Owner || original.Tenant != job.Tenant ||
            original.Policy.GroupShareChannelId.Length != 0 || original.Policy.SharingRosterDiscoveryOnly)
            throw new LabException("Private request's original audience/owner is no longer approved.");
        if (!personal) return;
        MessageActivity destination = PrivateContext.Activity(job.Personal ?? throw new LabException("Private destination is not established."));
        RequirePersonal(destination, job, PrivatePolicyReference.StoredPersonal);
    }
    private static void ObserveReference(PrivatePolicyReference reference, MessageActivity activity, PrivateJob job) =>
        PrivateIngressDiagnostic.ObservePolicyReference(reference, LabSettings.SameGuid(activity.From?.AadObjectId, job.Owner),
            reference == PrivatePolicyReference.OriginalChannel || job.Personal is null ? null :
                PrivateContext.Activity(job.Personal.Value).Conversation?.Id == activity.Conversation?.Id,
            LabSettings.SameGuid(activity.ChannelData?.Tenant?.Id, job.Tenant));
    internal Invocation RequirePersonal(MessageActivity activity, PrivateJob job,
        PrivatePolicyReference reference = PrivatePolicyReference.PersonalCandidate)
    {
        ObserveReference(reference, activity, job);
        PrivateContext.RequireSender(activity, teams);
        PrivateContext.RequirePersonalMetadata(activity, teams);
        Invocation invocation = policy.Authorize(activity, teams);
        if (invocation.Type != "personal" || invocation.Requester != job.Owner || invocation.Tenant != job.Tenant ||
            invocation.Policy.GroupShareChannelId.Length != 0 || invocation.Policy.SharingRosterDiscoveryOnly)
            throw new LabException("Private action must come from the matching approved personal-chat human.");
        if (job.Personal is { } stored &&
            PrivateContext.Activity(stored).Conversation!.Id != activity.Conversation!.Id)
            throw new LabException("Private request belongs to a different personal conversation.");
        return invocation;
    }
    internal MessageActivity CardReference(MessageActivity actual, PrivateJob job)
    {
        if (actual.Conversation?.ConversationType != "groupChat") return actual;
        // Only a strictly parsed private card invoke can use this server-established destination.
        // The invoke does not supply or redefine the personal route or the original request.
        Check(job);
        ObserveReference(PrivatePolicyReference.IncomingAction, actual, job);
        PrivateContext.RequireSender(actual, teams);
        MessageActivity stored = PrivateContext.Activity(job.Personal!.Value);
        if (job.MessageId is null || actual.Conversation.Id != stored.Conversation!.Id ||
            !LabSettings.SameGuid(actual.From?.AadObjectId, job.Owner) ||
            !LabSettings.SameGuid(actual.ChannelData?.Tenant?.Id, job.Tenant) ||
            actual.ChannelId != stored.ChannelId || actual.ServiceUrl != stored.ServiceUrl)
            throw new LabException("Private card invoke does not match its established personal destination.");
        // Explicitly approved compatibility policy: trust the established personal route for
        // this callback's group flag, not for new destinations or ordinary incoming messages.
        PrivateContext.RequirePersonalMetadata(actual, teams, trustBoundCallbackGroupFlag: true);
        if (actual.Conversation.IsGroup == true)
            PrivateIngressDiagnostic.ObserveBoundCallbackGroupFlag();
        return stored;
    }
    internal PrivateJob Approve(string id, int revision, MessageActivity actualPersonal)
    {
        return store.Change(id, job =>
        {
            Check(job); RequirePersonal(actualPersonal, job, PrivatePolicyReference.IncomingAction);
            if (job.State != PrivateJobState.AwaitingApproval || job.Revision != revision)
                throw new LabException("Private approval expired or was consumed.");
            return job with
            {
                Personal = PrivateContext.Capture(actualPersonal), State = PrivateJobState.AwaitingSignIn,
                Revision = checked(job.Revision + 1), ConsentUntil = clock.GetUtcNow().AddMinutes(5), HumanGeneration = null, Failure = null
            };
        });
    }
    internal PrivateGrant Grant(PrivateJob job)
    {
        Check(job);
        return new(job.Id, job.Revision, job.ImmutableKey,
            policy.Authorize(PrivateContext.Activity(job.Personal!.Value), teams).Binding, job.ConsentScope);
    }
    internal void ValidateGrant(PrivateGrant grant, HumanKey key, string personalBinding)
    {
        PrivateJob job = store.Get(grant.Job); Check(job);
        if (job.State != PrivateJobState.AwaitingSignIn || job.Revision != grant.Revision || job.ConsentScope != grant.ConsentScope ||
            job.ImmutableKey != grant.TaskHash || job.ConsentUntil <= clock.GetUtcNow() || job.ConsentUntil is null ||
            key != human.Key(policy.Authorize(PrivateContext.Activity(job.Personal!.Value), teams)) ||
            personalBinding != grant.PersonalBinding ||
            policy.Authorize(PrivateContext.Activity(job.Personal.Value), teams).Binding != personalBinding)
            throw new LabException("Private sign-in correlation was revoked, changed or expired.");
    }
    internal void Ready(PrivateGrant grant, long generation)
    {
        store.Change(grant.Job, job =>
        {
            Invocation personal = policy.Authorize(PrivateContext.Activity(job.Personal!.Value), teams);
            ValidateGrant(grant, human.Key(personal), personal.Binding);
            return job with { State = PrivateJobState.Ready, HumanGeneration = generation };
        });
    }
    internal void CheckRunning(string id, long generation)
    {
        PrivateJob job = store.Get(id); Check(job);
        if (job.State is not (PrivateJobState.Running or PrivateJobState.DeliveryPending) || job.HumanGeneration != generation)
            throw new LabException("Private execution authorization changed; no further dispatch or delivery.");
    }
    internal void Register(string id, CancellationTokenSource cancellation)
    {
        if (!running.TryAdd(id, cancellation)) throw new LabException("Private request is already running.");
    }
    internal void Release(string id) => running.TryRemove(id, out _);
    internal void Cancel(string id)
    {
        if (running.TryGetValue(id, out var cancellation)) cancellation.Cancel();
        store.Change(id, job => job.Terminal ? job : job with
        { State = PrivateJobState.Cancelled, Revision = checked(job.Revision + 1), HumanGeneration = null, ConsentUntil = null, Result = null });
    }
    internal void Disconnect(HumanKey key)
    {
        foreach (PrivateJob job in store.List().Where(j => j.Tenant == key.Tenant && j.Owner == key.User && !j.Terminal))
            Cancel(job.Id);
    }
}
