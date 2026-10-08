using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Identity.Client;
using ModelContextProtocol;

namespace WorkIqFiles;

internal sealed class WorkIqSessionFactory
{
    private readonly Func<IWorkIqTokenProvider, WorkIqSession> create;
    internal WorkIqSessionFactory(Func<IWorkIqTokenProvider, WorkIqSession> create) => this.create = create;
    internal WorkIqSession Create(IWorkIqTokenProvider tokens) => create(tokens);
}
internal sealed record SignInFlow(HumanKey Key, long Generation, DateTimeOffset Expires, string PersonalContextBinding,
    PrivateGrant? PrivateGrant = null);

// Account leases serialize callbacks, disconnect and entire file commands for each human.
// Different humans never share an MCP transport, token principal, or operation gate.
internal sealed class HumanConnections(HumanSettings settings, IHumanTokenCache tokens,
    WorkIqSessionFactory sessions, TimeProvider clock,
    PrivateAuthorizations? privateAuthorizations = null) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<HumanKey, Account> accounts = [];
    private readonly Dictionary<string, SignInFlow> links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SignInFlow> flows = new(StringComparer.Ordinal);
    private readonly Dictionary<PrivateGrant, DateTimeOffset> privateIssued = [];
    private bool disposed;

    private sealed class Account
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal ClaimsPrincipal? Principal;
        internal HumanTokens? Tokens;
        internal WorkIqSession? Session;
        internal long Generation;
    }
    private Account Get(HumanKey key)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!accounts.TryGetValue(key, out Account? account))
            {
                if (accounts.Count >= 64) throw new LabException("Human account limit reached; restart this lab after disconnecting test users.");
                accounts[key] = account = new();
            }
            return account;
        }
    }
    internal async Task<string> CreateLink(Invocation invocation, CancellationToken ct)
        => await CreateLinkCore(invocation, null, ct);
    internal Task<string> CreatePrivateLink(Invocation invocation, PrivateGrant grant, CancellationToken ct) =>
        CreateLinkCore(invocation, grant, ct);
    internal async Task<string?> PreparePrivateApproval(Invocation invocation, Func<PrivateGrant> approve, CancellationToken ct)
    {
        if (invocation.Type != "personal") throw new LabException("Private approval requires a personal context.");
        HumanKey key = settings.Key(invocation);
        Account account = Get(key);
        await account.Gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (account.Principal is not null) settings.ValidatePrincipal(account.Principal, key);
            lock (sync)
            {
                PurgeExpired();
                // Check capacity before consuming approval; no token, model, WorkIQ or Teams HTTP call here.
                if (account.Principal is null && (links.Count + flows.Count >= 100 || privateIssued.Count >= 100))
                    throw new LabException("Sign-in queue full; wait for link expiry.");
                PrivateGrant grant = approve();
                PrivateAuthorizations authorization = privateAuthorizations ?? throw new LabException("Private sign-in unavailable.");
                authorization.ValidateGrant(grant, key, invocation.Binding);
                if (account.Principal is not null)
                {
                    authorization.Ready(grant, account.Generation);
                    return null;
                }
                var existing = links.FirstOrDefault(p => p.Value.PrivateGrant == grant &&
                    p.Value.Key == key && p.Value.Generation == account.Generation &&
                    p.Value.PersonalContextBinding == invocation.Binding);
                if (existing.Key is not null) return LinkPrompt(existing.Key, grant);
                if (flows.Values.Any(f => f.PrivateGrant == grant && f.Key == key && f.Generation == account.Generation))
                    return "Private sign-in is already in progress. Complete the existing browser flow; no new link or analysis was started.";
                if (privateIssued.ContainsKey(grant))
                    return "The private sign-in link was already used. No new link or analysis was started; use /private-jobs if sign-in did not finish.";
                string id = RandomId();
                DateTimeOffset expires = clock.GetUtcNow().AddMinutes(5);
                links.Add(id, new(key, account.Generation, expires, invocation.Binding, grant));
                privateIssued.Add(grant, expires);
                return LinkPrompt(id, grant);
            }
        }
        finally { account.Gate.Release(); }
    }
    private async Task<string> CreateLinkCore(Invocation invocation, PrivateGrant? grant, CancellationToken ct)
    {
        if (invocation.Type != "personal") throw new LabException("Open an approved personal chat with this AU and send /signin. No sign-in link is published in shared conversations.");
        HumanKey key = settings.Key(invocation);
        if (grant is not null)
            (privateAuthorizations ?? throw new LabException("Private sign-in unavailable.")).ValidateGrant(grant, key, invocation.Binding);
        Account account = Get(key);
        await account.Gate.WaitAsync(ct);
        try
        {
            if (account.Principal is not null) return "Already connected as this verified human. Use /disconnect before signing in again.";
            lock (sync)
            {
                PurgeExpired();
                if (links.Count + flows.Count >= 100) throw new LabException("Sign-in queue full; wait for link expiry.");
                string id = RandomId();
                links.Add(id, new(key, account.Generation, clock.GetUtcNow().AddMinutes(5), invocation.Binding, grant));
                return LinkPrompt(id, grant);
            }
        }
        finally { account.Gate.Release(); }
    }
    private string LinkPrompt(string id, PrivateGrant? grant) =>
        "Sign in to WorkIQ as the same human account you use in Teams. This private, one-use link expires in five minutes:\n" +
        new Uri(settings.PublicBaseUrl, HumanSettings.StartPath + "?ticket=" + id).AbsoluteUri +
        (grant is null ? "\nNo file action will run on sign-in. Return to Teams and send your command again." :
            "\nOnly your separately approved request and its fixed consent scope may resume after matching sign-in; no capability upgrade is granted here.");
    internal string Begin(string ticket)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            PurgeExpired();
            if (!links.Remove(ticket, out SignInFlow? flow) || flow.Expires <= clock.GetUtcNow())
                throw new LabException("Sign-in link expired or was already used. Request /signin again in your personal chat.");
            string id = RandomId();
            flows.Add(id, flow);
            return id;
        }
    }
    internal void Abandon(string? flow)
    {
        if (flow is null) return;
        lock (sync) flows.Remove(flow);
    }
    internal async Task Complete(string flowId, ClaimsPrincipal validatedPrincipal, CancellationToken ct)
    {
        await using HumanCallbackLease lease = await BeginCallback(flowId, ct);
        lease.ObserveValidatedPrincipal(validatedPrincipal);
        await lease.Complete(validatedPrincipal, ct);
    }
    internal async Task<HumanCallbackLease> BeginCallback(string flowId, CancellationToken ct)
    {
        SignInFlow flow;
        lock (sync)
        {
            if (!flows.Remove(flowId, out SignInFlow? found) || found.Expires <= clock.GetUtcNow())
                throw new LabException("Sign-in flow expired, was used or was disconnected.");
            flow = found;
        }
        Account account = Get(flow.Key);
        await account.Gate.WaitAsync(ct);
        try
        {
            if (account.Generation != flow.Generation || account.Principal is not null || flow.Expires <= clock.GetUtcNow())
                throw new LabException("Sign-in was invalidated; request a new private sign-in link.");
            void CheckPrivate()
            {
                if (flow.PrivateGrant is { } grant)
                    (privateAuthorizations ?? throw new LabException("Private sign-in unavailable."))
                        .ValidateGrant(grant, flow.Key, flow.PersonalContextBinding);
            }
            CheckPrivate();
            long acceptedGeneration = 0;
            return new(settings, flow.Key, async (validatedPrincipal, cancellation) =>
            {
                CheckPrivate();
                if (flow.Expires <= clock.GetUtcNow()) throw new LabException("Sign-in expired during callback.");
                settings.ValidatePrincipal(validatedPrincipal, flow.Key);
                ClaimsPrincipal ownPrincipal = new(validatedPrincipal.Identities.Select(i => new ClaimsIdentity(i)));
                HumanTokens ownTokens = new(settings, flow.Key, ownPrincipal, tokens);
                await ownTokens.GetTokenAsync(cancellation);
                CheckPrivate();
                account.Principal = ownPrincipal;
                account.Tokens = ownTokens;
                account.Session = sessions.Create(ownTokens);
                account.Generation++;
                acceptedGeneration = account.Generation;
            }, async rejectedPrincipal =>
            {
                try
                {
                    if (rejectedPrincipal is not null) await tokens.Remove(rejectedPrincipal);
                }
                finally { account.Gate.Release(); }
            }, () =>
            {
                if (flow.PrivateGrant is { } grant) privateAuthorizations!.Ready(grant, acceptedGeneration);
            }) { PrivateContinuation = flow.PrivateGrant is not null };
        }
        catch { account.Gate.Release(); throw; }
    }
    internal async Task<HumanLease> Acquire(Invocation invocation, CancellationToken ct)
    {
        HumanKey key = settings.Key(invocation);
        Account account = Get(key);
        await account.Gate.WaitAsync(ct);
        try
        {
            if (account.Principal is null || account.Session is null) throw new HumanSignInRequiredException();
            settings.ValidatePrincipal(account.Principal, key);
            return new(account.Session, account.Gate, account.Generation);
        }
        catch { account.Gate.Release(); throw; }
    }
    internal async Task<long?> Generation(Invocation invocation, CancellationToken ct)
    {
        Account account = Get(settings.Key(invocation));
        await account.Gate.WaitAsync(ct);
        try
        {
            if (account.Principal is null) return null;
            settings.ValidatePrincipal(account.Principal, settings.Key(invocation));
            return account.Generation;
        }
        finally { account.Gate.Release(); }
    }
    internal async Task<string> Status(Invocation invocation, CancellationToken ct)
    {
        Account account = Get(settings.Key(invocation));
        await account.Gate.WaitAsync(ct);
        try { return account.Principal is null ? "Human WorkIQ disconnected; /signin in approved personal chat." : "Human WorkIQ connected to this requester; Teams messaging remains AU."; }
        finally { account.Gate.Release(); }
    }
    internal Task<string> Disconnect(Invocation invocation) => Disconnect(settings.Key(invocation));
    private async Task<string> Disconnect(HumanKey key, bool revokeJobs = true)
    {
        // Cancel private jobs before waiting for the human operation gate they may currently hold.
        if (revokeJobs) privateAuthorizations?.Disconnect(key);
        Account account = Get(key);
        await account.Gate.WaitAsync();
        try
        {
            account.Generation++;
            lock (sync)
            {
                foreach (string id in links.Where(p => p.Value.Key == key).Select(p => p.Key).ToArray()) links.Remove(id);
                foreach (string id in flows.Where(p => p.Value.Key == key).Select(p => p.Key).ToArray()) flows.Remove(id);
            }
            ClaimsPrincipal? principal = account.Principal;
            WorkIqSession? session = account.Session;
            HumanTokens? provider = account.Tokens;
            account.Principal = null; account.Session = null; account.Tokens = null;
            bool cleanupFailed = false;
            try
            {
                if (session is not null) await session.DisposeAsync();
            }
            catch (Exception error) when (error is HttpRequestException or McpException or OperationCanceledException or HumanSignInRequiredException)
            { cleanupFailed = true; }
            finally
            {
                if (provider is not null) provider.Revoked = true;
                if (principal is not null)
                {
                    try { await tokens.Remove(principal); }
                    catch (Exception error) when (error is MsalException or InvalidOperationException) { cleanupFailed = true; }
                }
            }
            return cleanupFailed
                ? "Human connection invalidated locally; remote session/cache cleanup failed. No fallback. Sign-in required; restart this lab before further tests if cache cleanup remains uncertain."
                : "Human WorkIQ disconnected; account cache and pending sign-ins removed. Other humans, AU native mode and Teams AU messaging are unchanged.";
        }
        finally { account.Gate.Release(); }
    }
    private void PurgeExpired()
    {
        foreach (string id in links.Where(p => p.Value.Expires <= clock.GetUtcNow()).Select(p => p.Key).ToArray()) links.Remove(id);
        foreach (string id in flows.Where(p => p.Value.Expires <= clock.GetUtcNow()).Select(p => p.Key).ToArray()) flows.Remove(id);
        foreach (PrivateGrant grant in privateIssued.Where(p => p.Value <= clock.GetUtcNow()).Select(p => p.Key).ToArray())
            privateIssued.Remove(grant);
    }
    private static string RandomId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public async ValueTask DisposeAsync()
    {
        HumanKey[] keys;
        lock (sync) { if (disposed) return; keys = accounts.Keys.ToArray(); }
        foreach (HumanKey key in keys) await Disconnect(key, revokeJobs: false);
        lock (sync) { disposed = true; links.Clear(); flows.Clear(); privateIssued.Clear(); accounts.Clear(); }
    }
}
internal sealed class HumanCallbackLease(HumanSettings settings, HumanKey key,
    Func<ClaimsPrincipal, CancellationToken, Task> complete, Func<ClaimsPrincipal?, Task> release,
    Action? afterAcceptedRelease = null) : IAsyncDisposable
{
    internal bool PrivateContinuation { get; init; }
    private ClaimsPrincipal? observed;
    private bool accepted;
    private int disposed;
    internal void ObserveValidatedPrincipal(ClaimsPrincipal principal)
    {
        settings.ValidatePrincipal(principal, key);
        observed = principal;
    }
    internal async Task Complete(ClaimsPrincipal principal, CancellationToken ct)
    {
        await complete(principal, ct);
        accepted = true;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            await release(accepted ? null : observed);
            if (accepted) afterAcceptedRelease?.Invoke();
        }
    }
}
internal sealed class HumanLease(IWorkIq backend, SemaphoreSlim gate, long generation) : IAsyncDisposable
{
    internal IWorkIq Backend => backend;
    internal long Generation => generation;
    public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
}
