using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Apps.Handlers;

namespace WorkIqFiles;

internal sealed class PrivateAnalysisCoordinator(PrivateAnalysisOptions options, PrivateJobStore store,
    PrivateAuthorizations authorization, HumanConnections humans, HumanSettings humanSettings,
    IPrivateTeams teams, PrivateIntentRouter intent, FilePolicy policy, LabSettings settings, TimeProvider clock,
    ILogger<PrivateAnalysisCoordinator> logger, NaturalLanguageOptions? naturalOptions = null,
    WorkIqHostPolicy? hostPolicy = null)
{
    internal const string Neutral = "A private continuation is available. Open the agent's approved personal chat and send /private-jobs to continue. No private result or sign-in link will be posted here.";
    internal const string CardSent = "A private authorization card was sent. Review its request and scope in the agent's personal chat, approve it and sign in there if prompted; work then continues automatically. Results return to that established route.";
    private readonly SemaphoreSlim ingress = new(1, 1);
    private readonly SemaphoreSlim actions = new(1, 1);
    internal async Task<PrivateIngress> Handle(MessageActivity activity, CancellationToken ct)
    {
        if (naturalOptions?.DirectMcp.UseTeamsMcp == true) return new(true, ProviderIsolation.WorkIqOnly);
        if (hostPolicy is { PrivateJobsEnabled: false })
            return new(true, "Private jobs are inactive. " + hostPolicy.Status);
        if (!options.Enabled) return new(false, null);
        string text = activity.Text ?? "";
        if (text.Length == 0) return new(false, null);
        string commandText = WorkIqIngress.ControlText(activity);
        bool privateCommand = commandText == "/private-jobs";
        if (!privateCommand && WorkIqIngress.IsOperationalOrSlash(activity)) return new(false, null);
        PrivateIngressDiagnostic.Enter(PrivateIngressStage.ContextAuthorization);
        Invocation invocation = policy.Authorize(activity, settings);
        if (privateCommand && invocation.Type != "personal") return new(true, Neutral);
        if (!privateCommand && invocation.Type != "channel") return new(false, null);
        if (!privateCommand && naturalOptions is { } configuration &&
            (!configuration.Enabled || !configuration.DirectMcp.Enabled || !configuration.FullWorkIqGuide))
            return new(true, NaturalLanguageOptions.WorkIqActivation);
        PrivateIngressDiagnostic.Enter(PrivateIngressStage.SenderValidation);
        PrivateContext.RequireSender(activity, settings);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        PrivateIngressDiagnostic.Enter(PrivateIngressStage.QueueWait);
        await ingress.WaitAsync(deadline.Token);
        try
        {
            if (privateCommand)
            {
                PrivateIngressDiagnostic.Enter(PrivateIngressStage.RecoveryLookup);
                PrivateJob[] matching = store.List().Where(j => j.Owner == invocation.Requester && j.Tenant == invocation.Tenant).ToArray();
                PrivateJob[] pending = matching.Where(j => !j.Terminal).ToArray();
                if (pending.Length == 0)
                {
                    PrivateIngressDiagnostic.Current?.LogEmptyRecovery(logger, matching.Length != 0);
                    return new(true, "No unexpired private requests. Interrupted/completed requests never re-run automatically.");
                }
                int offered = 0;
                foreach (PrivateJob pendingJob in pending.Take(5))
                {
                    if (PrivateIngressDiagnostic.Current is { } recovery) recovery.Persistence = PrivatePersistence.Existing;
                    PrivateIngressDiagnostic.Enter(PrivateIngressStage.RecoveryAuthorization);
                    authorization.Check(pendingJob, personal: false);
                    authorization.RequirePersonal(activity, pendingJob);
                    if (pendingJob.State is PrivateJobState.Running or PrivateJobState.Ready)
                    {
                        PrivateIngressDiagnostic.Enter(PrivateIngressStage.StatusSend);
                        await teams.Send(activity, PrivateCards.Status(pendingJob), deadline.Token);
                        offered++;
                        continue;
                    }
                    PrivateIngressDiagnostic.Enter(PrivateIngressStage.RecoveryPersist);
                    PrivateJob job = store.Change(pendingJob.Id, j => j with
                    {
                        Personal = PrivateContext.Capture(activity), State = PrivateJobState.AwaitingApproval,
                        Revision = checked(j.Revision + 1), HumanGeneration = null, ConsentUntil = null
                    });
                    await DeliverApproval(job, activity, deadline.Token, explicitRecovery: true);
                    offered++;
                }
                return new(true, offered == 0 ? "Private analysis is already queued/running; it is not replayed." :
                    "Review the private authorization card(s). Signing in without approving a specific request does not run analysis.");
            }
            // Store the original bounded request; no business-content rewriting.
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.InputValidation);
            NaturalLanguageBudgets.Require("PrivateInputChars", text.Length, 16000);
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.AttachmentMetadata);
            string metadata = DirectMessageContext.Create(activity, invocation);
            string sourceId = activity.Id ?? "";
            if (sourceId.Length == 0) throw new LabException("Private routing requires the real current activity ID.");
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.ReplayLookup);
            if (store.List().Any(j => j.Tenant == invocation.Tenant && j.Owner == invocation.Requester &&
                PrivateContext.Activity(j.Origin).Id == sourceId &&
                PrivateContext.Activity(j.Origin).Conversation!.Id == activity.Conversation!.Id))
            {
                if (PrivateIngressDiagnostic.Current is { } replay) replay.Persistence = PrivatePersistence.Existing;
                return new(true, Neutral);
            }
            bool handoff;
            double ms;
            try { (handoff, ms) = await intent.Route(text, deadline.Token); }
            catch (WorkIqIntentClarification clarification)
            {
                return new(true, "WorkIQ request needs clarification: " + clarification.Message +
                    "\nNo data-tool call, identity change or private job created.");
            }
            catch (WorkIqProfileMismatch mismatch)
            {
                return new(true, "WorkIQ caller/profile mismatch: " + mismatch.Message +
                    "\nNo data-tool call, identity change or private job created.");
            }
            if (!handoff) return new(false, null);
            // Intent choice grants nothing and cannot supply a recipient, task body or resource identity.
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.JobPersist);
            if (PrivateIngressDiagnostic.Current is { } adding) adding.Persistence = PrivatePersistence.Unconfirmed;
            DateTimeOffset created = clock.GetUtcNow();
            PrivateJob jobNew = store.Add(new()
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
                Tenant = invocation.Tenant, Owner = invocation.Requester, Request = text,
                Metadata = "Original channel metadata, untrusted provenance only; no personal file authority:\n" + metadata,
                Origin = PrivateContext.Capture(activity), Configuration = authorization.Configuration,
                Created = created, Expires = created.AddMinutes(30),
                State = PrivateJobState.AwaitingRoute, RoutingMs = ms,
                ConsentScope = PrivateConsentScope.NativeReadWriteV1
            });
            if (PrivateIngressDiagnostic.Current is { } added) added.Persistence = PrivatePersistence.Confirmed;
            try
            {
                PrivateIngressDiagnostic.Enter(PrivateIngressStage.RememberedRouteLookup);
                JsonElement? known = store.List().Where(j => j.Owner == jobNew.Owner && j.Tenant == jobNew.Tenant && j.Personal is not null)
                    .OrderByDescending(j => j.Created).Select(j => j.Personal).FirstOrDefault();
                if (PrivateIngressDiagnostic.Current is { } routing)
                    routing.RouteSource = known is null ? PrivateRouteSource.Create : PrivateRouteSource.Remembered;
                MessageActivity personal = known is { } reference ? PrivateContext.Activity(reference) :
                    await teams.CreatePersonal(activity, deadline.Token);
                PrivateIngressDiagnostic.Enter(PrivateIngressStage.PersonalBinding);
                authorization.RequirePersonal(personal, jobNew);
                PrivateIngressDiagnostic.Enter(PrivateIngressStage.PersonalPersist);
                jobNew = store.Change(jobNew.Id, j => j with
                { Personal = PrivateContext.Capture(personal), State = PrivateJobState.AwaitingApproval });
                await DeliverApproval(jobNew, personal, deadline.Token);
                return new(true, CardSent);
            }
            catch (Exception error) when (SafeFailure(error))
            {
                logger.LogWarning("Private route/card delivery not established; category={Category}. Details withheld; personal-chat recovery required.", Category(error));
                PrivateIngressDiagnostic.Current?.Log(logger, error, routeFallback: true);
                // The persisted job remains available to this exact user; no create/send replay or channel auth link.
            }
            return new(true, Neutral);
        }
        finally { ingress.Release(); }
    }
    private async Task DeliverApproval(PrivateJob job, MessageActivity personal, CancellationToken ct, bool explicitRecovery = false)
    {
        PrivateIngressDiagnostic.Enter(PrivateIngressStage.ApprovalAuthorization);
        authorization.Check(job);
        if (job.MessageId is { } id)
        {
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.ApprovalUpdate);
            await teams.Update(personal, id, PrivateCards.Approval(job), ct);
        }
        else
        {
            if (job.Failure == "Initial private send unconfirmed." && !explicitRecovery)
                throw new LabException("Initial private send is not automatically replayed.");
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.ApprovalSendMarker);
            store.Change(job.Id, j => j with { Failure = "Initial private send unconfirmed." });
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.ApprovalSend);
            string own = await teams.Send(personal, PrivateCards.Approval(job), ct);
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.ApprovalAcknowledgment);
            store.Change(job.Id, j => j with { MessageId = own, Failure = null });
        }
    }
    internal Task<PrivateActionResult> Action(MessageActivity actual, string verb, string id, int revision, CancellationToken ct) =>
        ActionCore(actual, verb, id, revision, ct, boundCardInvoke: false);
    private async Task<PrivateActionResult> ActionCore(MessageActivity actual, string verb, string id, int revision,
        CancellationToken ct, bool boundCardInvoke)
    {
        if (naturalOptions?.DirectMcp.UseTeamsMcp == true) return new(ProviderIsolation.WorkIqOnly);
        if (hostPolicy is { PrivateJobsEnabled: false })
            throw new LabException("Private jobs are inactive; no authorization or stored job was accessed.");
        if (!options.Enabled || verb is not ("lab13.private.approve" or "lab13.private.cancel"))
            throw new LabException("Private action unavailable.");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        PrivateIngressDiagnostic.Enter(PrivateIngressStage.InvokeQueue);
        await actions.WaitAsync(deadline.Token);
        try
        {
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.InvokeAuthorization);
            PrivateJob job = store.Get(id);
            authorization.Check(job);
            MessageActivity reference = boundCardInvoke ? authorization.CardReference(actual, job) : actual;
            Invocation personal = authorization.RequirePersonal(reference, job, PrivatePolicyReference.IncomingAction);
            bool sameApproval = verb == "lab13.private.approve" && job.Revision == (long)revision + 1 &&
                job.State is PrivateJobState.AwaitingSignIn or PrivateJobState.Ready or PrivateJobState.Running or PrivateJobState.DeliveryPending;
            if (job.Revision != revision && !sameApproval) throw new LabException("Private card is expired or was already used.");
            if (verb == "lab13.private.cancel")
            { authorization.Cancel(id); return new("Private request cancelled; no automatic replay. Already dispatched operations may finish remotely; prior effects are not undone."); }
            if (sameApproval && job.State != PrivateJobState.AwaitingSignIn)
                return new("This private approval was already accepted. No additional analysis was queued.");
            if (job.MessageId is null) throw new LabException("Private card delivery must be established before sign-in.");
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.InvokeHumanGate);
            string? response = await humans.PreparePrivateApproval(personal, () =>
            {
                PrivateIngressDiagnostic.Enter(PrivateIngressStage.InvokeConsent);
                reference = boundCardInvoke ? authorization.CardReference(actual, store.Get(id)) : actual;
                job = sameApproval ? store.Get(id) : authorization.Approve(id, revision, reference);
                PrivateGrant grant = authorization.Grant(job);
                PrivateIngressDiagnostic.Enter(PrivateIngressStage.InvokeSignInPreparation);
                return grant;
            }, deadline.Token);
            if (response is null) return new("Approved private request queued under " + job.ConsentScope + ". The original channel receives no result.");
            string? url = BrowserSignIn.PromptUrl(response, humanSettings);
            return url is null ? new(response) :
                new("Use the private sign-in card. Only the same tenant/user can continue the approved request.", PrivateCards.SignIn(url, job.ConsentScope));
        }
        finally { actions.Release(); }
    }
    internal Task<PrivateActionResult> Action(InvokeActivity activity, CancellationToken ct)
    {
        PrivateIngressDiagnostic.Enter(PrivateIngressStage.InvokeParse);
        if (activity.Name != InvokeNames.AdaptiveCardAction)
            throw new LabException("Private action requires an adaptive-card invoke.");
        AdaptiveCardActionValue? value = activity.Value?.Deserialize<AdaptiveCardActionValue>();
        AdaptiveCardAction? action = value?.Action;
        if (action?.Type != "Action.Execute" || value?.Trigger is not (null or "manual") ||
            action.Data is null || action.Data.Count != 2 || !action.Data.ContainsKey("job") || !action.Data.ContainsKey("revision"))
            throw new LabException("Private approval requires an explicit manual card action with a bound job/revision.");
        JsonElement data = JsonSerializer.SerializeToElement(action.Data);
        if (!data.GetProperty("revision").TryGetInt32(out int revision))
            throw new LabException("Private action revision is unavailable.");
        MessageActivity actual = JsonSerializer.Deserialize<MessageActivity>(
            JsonSerializer.Serialize(activity, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        return ActionCore(actual, action.Verb ?? "", FileCommand.Required(data, "job"), revision, ct, boundCardInvoke: true);
    }
    internal static bool SafeFailure(Exception error) => error is LabException or HttpRequestException or OperationCanceledException or
        IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or System.ClientModel.ClientResultException or
        Microsoft.Identity.Client.MsalException or Microsoft.Identity.Web.MicrosoftIdentityWebChallengeUserException;
    internal static string Category(Exception error) => error is HttpRequestException ? "HTTP" :
        error is OperationCanceledException ? "cancelled" : "validation-or-provider";
}
