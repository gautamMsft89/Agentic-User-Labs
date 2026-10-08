namespace WorkIqFiles;

internal sealed class PrivateAnalysisWorker(PrivateJobStore store, PrivateAuthorizations authorization,
    HumanConnections humans, WorkIqRouter router, IPrivateTeams teams, NaturalLanguageOptions options,
    FilePolicy policy, LabSettings settings, SetupMode setup, ChannelSetup channelSetup, TimeProvider clock,
    ILogger<PrivateAnalysisWorker> logger, Func<NaturalLanguageOptions, NaturalLanguageModel>? modelFactory = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            do { await RunOne(stoppingToken); }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        // Store corruption/persistence failures propagate and stop the host; never continue with an empty replacement queue.
    }
    internal async Task<bool> RunOne(CancellationToken host)
    {
        if (options.DirectMcp.UseTeamsMcp || !router.PrivateJobsAllowed || !router.HumanIdentityAllowed) return false;
        PrivateJob? job = store.List().FirstOrDefault(j => j.State == PrivateJobState.Ready);
        if (job is null) return false;
        using CancellationTokenSource execution = CancellationTokenSource.CreateLinkedTokenSource(host);
        TimeSpan left = job.Expires - clock.GetUtcNow();
        if (left <= TimeSpan.Zero) return false;
        execution.CancelAfter(left);
        bool registered = false, claimed = false;
        try
        {
            options.RequireNativeWorkIq();
            authorization.Check(job);
            if (setup.Enabled || channelSetup.Enabled) throw new LabException("Private analysis is unavailable during setup.");
            var personal = PrivateContext.Activity(job.Personal!.Value, job.Request);
            var invocation = policy.Authorize(personal, settings);
            long? generation = await humans.Generation(invocation, execution.Token);
            if (generation is null || generation != job.HumanGeneration)
            {
                store.Change(job.Id, j => j with
                { State = PrivateJobState.AwaitingApproval, Revision = checked(j.Revision + 1), HumanGeneration = null, ConsentUntil = null });
                return true;
            }
            job = store.Change(job.Id, j =>
            {
                authorization.Check(j);
                if (j.State != PrivateJobState.Ready || j.ConsentUntil is null || j.ConsentUntil <= clock.GetUtcNow())
                    throw new LabException("Private execution claim expired or was consumed.");
                if (j.HasRun && j.Result is null) throw new LabException("Private analysis already claimed; no replay.");
                return j with { State = j.Result is null ? PrivateJobState.Running : PrivateJobState.DeliveryPending, HasRun = true };
            });
            claimed = true;
            authorization.Register(job.Id, execution); registered = true;
            void Revalidate()
            {
                if (options.DirectMcp.UseTeamsMcp)
                    throw new LabException("Private WorkIQ execution blocked by TeamsMcp provider; no continuation/fallback.");
                options.RequireNativeWorkIq();
                authorization.CheckRunning(job.Id, generation.Value);
            }
            Revalidate();
            using DirectProgress progress = new(personal, settings, policy,
                (_, _) => throw new LabException("Private worker never creates another message."),
                (id, content, ct) => teams.Update(personal, id, content, ct),
                execution.Token, logger, clock, job.MessageId ?? throw new LabException("Private outgoing message ID unavailable."));
            if (job.Result is null)
            {
                NaturalLanguageOptions isolated = new()
                {
                    Enabled = options.Enabled, ApiKey = options.ApiKey, Endpoint = options.Endpoint,
                    Deployment = options.Deployment, ReasoningEffort = options.ReasoningEffort,
                    Budgets = options.Budgets, FullWorkIqGuide = options.FullWorkIqGuide,
                    DirectMcp = new() { Enabled = options.DirectMcp.Enabled, Principal = "SignedInHuman" }
                };
                DirectMcpAgent analysis = new(isolated, router, policy, settings, setup, channelSetup, modelFactory, clock);
                string result = await analysis.Handle(personal, execution.Token, progress,
                    new(generation.Value, job.Metadata, Revalidate, job.ConsentScope));
                Revalidate();
                string summary = (job.ConsentScope == PrivateConsentScope.LegacyReadOnly ?
                    "PRIVATE LEGACY READ-ONLY ANALYSIS\n" : "PRIVATE HUMAN NATIVE WORK (READ/WRITE/ACTIONS)\n") + result +
                    $"\nPrivate input routing: {job.RoutingMs:F1} ms. Queue/approval/sign-in wait is separate from analysis timing. Results are not posted to the channel.";
                if (System.Text.Encoding.UTF8.GetByteCount(summary) > isolated.ResolveBudgets().ReplyBytes)
                    summary = "Private result exceeds reply budget; output withheld, not truncated. Analysis was attempted and is not replayed.";
                job = store.Change(job.Id, j =>
                {
                    Revalidate();
                    return j with { Result = summary, State = PrivateJobState.DeliveryPending };
                });
            }
            else await progress.StartAsync(execution.Token);
            Revalidate();
            await progress.CompleteAsync(job.Result!);
            Revalidate();
            if (progress.Delivered)
                store.Change(job.Id, j => j with { State = PrivateJobState.Completed, HumanGeneration = null, ConsentUntil = null });
            else logger.LogWarning("Private result delivery remains pending; reopen private /private-jobs. Analysis is not replayed.");
        }
        catch (Exception error) when (PrivateFailures.SafeFailure(error))
        {
            logger.LogWarning("Private job stopped; category={Category}. No analysis replay; details withheld.", PrivateFailures.Category(error));
            PrivateJob? latest = store.List().FirstOrDefault(j => j.Id == job.Id);
            if (latest is not null && !latest.Terminal && (claimed || latest.State is not (PrivateJobState.Running or PrivateJobState.DeliveryPending)))
                store.Change(job.Id, j => j with
                {
                    State = j.Result is not null ? PrivateJobState.DeliveryPending :
                        j.HasRun ? PrivateJobState.Interrupted : PrivateJobState.AwaitingApproval,
                    Revision = checked(j.Revision + 1), HumanGeneration = null, ConsentUntil = null,
                    Failure = error is LabException && error.Message == NaturalLanguageOptions.WorkIqActivation
                        ? NaturalLanguageOptions.WorkIqActivation
                        : "Stopped; private recovery requires renewed authorization, never an automatic analysis replay."
                });
        }
        finally
        {
            if (registered) authorization.Release(job.Id);
            if (execution.IsCancellationRequested && !host.IsCancellationRequested && job.MessageId is { } messageId &&
                job.Personal is { } personal)
            {
                using CancellationTokenSource delivery = CancellationTokenSource.CreateLinkedTokenSource(host);
                delivery.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await teams.Update(PrivateContext.Activity(personal), messageId,
                        new Microsoft.Teams.Apps.Schema.MessageActivity
                        { Text = "Private work cancelled or expired. No further operations or automatic replay; dispatched operations may finish remotely and prior effects are not undone." },
                        delivery.Token);
                }
                catch (Exception error) when (PrivateFailures.SafeFailure(error))
                { logger.LogWarning("Private cancellation delivery unconfirmed; details withheld."); }
            }
        }
        return true;
    }
}
