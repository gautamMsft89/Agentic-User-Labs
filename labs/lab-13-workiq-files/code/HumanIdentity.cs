using System.Security.Claims;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders;
using Microsoft.IdentityModel.JsonWebTokens;

namespace WorkIqFiles;

internal sealed class HumanSignInRequiredException() : LabException(
    "Human WorkIQ sign-in required. In an approved personal chat with this AU, send /signin, select your Teams account, then retry the command. No AU/app-only/Graph-token fallback.");

internal sealed class HumanSettings
{
    internal const string Scheme = "HumanWorkIQ";
    internal const string Cookies = "HumanWorkIQCookies";
    internal const string CallbackPath = "/signin-workiq-files";
    internal const string StartPath = "/auth/files/signin";
    internal const string Resource = "api://workiq.svc.cloud.microsoft";
    internal const string ResourceAppId = "fdcc1f02-fc51-4226-8753-f668596af7f7";
    internal static readonly string[] Scopes = [Resource + "/WorkIQAgent.Ask"];
    internal HumanSettings(IConfiguration configuration, LabSettings teams)
    {
        if (!Guid.TryParse(configuration["HumanWorkIQ:TenantId"], out Guid tenant) ||
            !LabSettings.SameGuid(tenant.ToString(), teams.TenantId))
            throw new InvalidOperationException("HumanWorkIQ:TenantId must match the authenticated Teams tenant.");
        if (!Guid.TryParse(configuration["HumanWorkIQ:ClientId"], out Guid client) || client == Guid.Empty ||
            LabSettings.SameGuid(client.ToString(), teams.BlueprintClientId) ||
            LabSettings.SameGuid(client.ToString(), teams.AgentIdentityClientId))
            throw new InvalidOperationException("HumanWorkIQ:ClientId must be a separate, explicitly configured human web OAuth client.");
        if (configuration["HumanWorkIQ:Instance"] != "https://login.microsoftonline.com/")
            throw new InvalidOperationException("HumanWorkIQ requires the public-cloud Entra instance.");
        if (string.IsNullOrWhiteSpace(configuration["HumanWorkIQ:ClientSecret"]))
            throw new InvalidOperationException("Supply the separate HumanWorkIQ:ClientSecret securely; no blueprint-credential fallback.");
        if (configuration["HumanWorkIQ:CallbackPath"] != CallbackPath)
            throw new InvalidOperationException($"HumanWorkIQ:CallbackPath must be {CallbackPath}.");
        if (!Uri.TryCreate(configuration["HumanWorkIQ:PublicBaseUrl"], UriKind.Absolute, out Uri? url) ||
            url.Scheme != "https" || !url.IsDefaultPort || url.AbsolutePath != "/" || url.UserInfo.Length != 0 ||
            url.Query.Length != 0 || url.Fragment.Length != 0 || url.Host is "localhost" or "127.0.0.1")
            throw new InvalidOperationException("HumanWorkIQ:PublicBaseUrl must be the explicitly trusted public HTTPS origin.");
        Tenant = tenant.ToString("D");
        Client = client.ToString("D");
        PublicBaseUrl = url;
        AgentUser = teams.AgentUserObjectId;
    }
    internal string Tenant { get; }
    internal string Client { get; }
    internal string AgentUser { get; }
    internal Uri PublicBaseUrl { get; }
    internal string RedirectUri => new Uri(PublicBaseUrl, CallbackPath).AbsoluteUri;
    internal string Issuer => $"https://login.microsoftonline.com/{Tenant}/v2.0";
    internal HumanKey Key(Invocation invocation) => new(invocation.Tenant, invocation.Requester, Client, Scopes[0], WorkIqSession.Endpoint);
    internal void ValidatePrincipal(ClaimsPrincipal principal, HumanKey key)
    {
        if (principal.Identity?.IsAuthenticated != true ||
            !LabSettings.SameGuid(principal.GetTenantId(), key.Tenant) ||
            !LabSettings.SameGuid(principal.GetObjectId(), key.User) ||
            !LabSettings.SameGuid(key.Tenant, Tenant) || key.Client != Client ||
            key.Scope != Scopes[0] || key.Endpoint != WorkIqSession.Endpoint ||
            LabSettings.SameGuid(key.User, AgentUser) || principal.IsAgentUserIdentity())
            throw new HumanSignInRequiredException();
    }
    internal void ValidateAccessToken(string token, HumanKey key, ClaimsPrincipal validatedPrincipal)
    {
        ValidatePrincipal(validatedPrincipal, key);
        // This is an access-token mix-up guard, NOT sign-in. OIDC validates signed ID tokens;
        // WorkIQ validates access-token signatures. Opaque or uninspectable tokens fail closed.
        JsonWebTokenHandler reader = new();
        if (!reader.CanReadToken(token) || token.Count(c => c == '.') != 2) throw new HumanSignInRequiredException();
        JsonWebToken jwt;
        try { jwt = reader.ReadJsonWebToken(token); }
        catch (ArgumentException) { throw new HumanSignInRequiredException(); }
        ClaimsPrincipal claims = new(new ClaimsIdentity(jwt.Claims));
        string? Claim(string name) => claims.FindFirst(name)?.Value;
        string? app = Claim("azp") ?? Claim("appid");
        if (!LabSettings.SameGuid(Claim("tid"), key.Tenant) || !LabSettings.SameGuid(Claim("oid"), key.User) ||
            !LabSettings.SameGuid(app, Client) || claims.IsAgentUserIdentity() ||
            (Claim("azp") is string azp && !LabSettings.SameGuid(azp, Client)) ||
            (Claim("appid") is string appid && !LabSettings.SameGuid(appid, Client)) ||
            !jwt.Audiences.Any(a => a is Resource or ResourceAppId) ||
            !(Claim("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("WorkIQAgent.Ask") ||
            jwt.ValidTo <= DateTime.UtcNow || jwt.ValidFrom > DateTime.UtcNow ||
            (jwt.Issuer != Issuer && jwt.Issuer != $"https://sts.windows.net/{Tenant}/"))
            throw new HumanSignInRequiredException();
    }
}

internal sealed record HumanKey(string Tenant, string User, string Client, string Scope, string Endpoint);
internal interface IWorkIqTokenProvider
{
    Task<string> GetTokenAsync(CancellationToken ct);
}
internal interface IHumanTokenCache
{
    Task<string> Acquire(ClaimsPrincipal user, CancellationToken ct);
    Task Remove(ClaimsPrincipal user);
}
internal sealed class HumanTokenCache(IServiceScopeFactory services) : IHumanTokenCache
{
    public async Task<string> Acquire(ClaimsPrincipal user, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using IServiceScope scope = services.CreateScope();
        try
        {
            // Explicit scheme AND explicit validated principal: never use the ambient Teams human/AU.
            return await scope.ServiceProvider.GetRequiredService<ITokenAcquisition>().GetAccessTokenForUserAsync(
                HumanSettings.Scopes, authenticationScheme: HumanSettings.Scheme,
                tenantId: user.GetTenantId()!, user: user,
                tokenAcquisitionOptions: new TokenAcquisitionOptions { CancellationToken = ct });
        }
        catch (Exception error) when (error is MicrosoftIdentityWebChallengeUserException or MsalException)
        { throw new HumanSignInRequiredException(); }
    }
    public async Task Remove(ClaimsPrincipal user)
    {
        using IServiceScope scope = services.CreateScope();
        string account = user.GetMsalAccountId()
            ?? throw new InvalidOperationException("Validated human principal lacks its MSAL account cache identifier.");
        await scope.ServiceProvider.GetRequiredService<IMsalTokenCacheProvider>().ClearAsync(account);
    }
}
internal sealed class HumanTokens(HumanSettings settings, HumanKey key, ClaimsPrincipal user, IHumanTokenCache cache) : IWorkIqTokenProvider
{
    internal HumanKey Key => key;
    internal bool Revoked;
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (Revoked) throw new HumanSignInRequiredException();
        settings.ValidatePrincipal(user, key);
        string token = await cache.Acquire(user, ct);
        ct.ThrowIfCancellationRequested();
        if (Revoked) throw new HumanSignInRequiredException();
        settings.ValidateAccessToken(token, key, user);
        return token;
    }
}
