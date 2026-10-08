using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.JsonWebTokens;

namespace WorkIqFiles;

internal sealed class AuWorkIqAuthenticationException() : LabException(
    "AU WorkIQ authentication failed; stage=AU token acquisition/validation; resourceStatus=unavailable; " +
    "errorCode=unavailable; requestId=unavailable. No resource authorization conclusion or alternate identity.");

internal sealed class AgentUserTokenProvider(IServiceScopeFactory services, LabSettings settings) : IWorkIqTokenProvider
{
    internal const string AuthenticationOptionsName = "WorkIQAgent:Blueprint";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ClaimsPrincipal agentUser = new();
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        void DiagnoseCancellation()
        {
            if (OperationProgress.Current is { } progress && timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                progress.CancellationSource = "60s AU token budget (includes token queue)";
        }
        try { await gate.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { DiagnoseCancellation(); throw; }
        try
        {
            using IServiceScope scope = services.CreateScope();
            AuthorizationHeaderProviderOptions options = new AuthorizationHeaderProviderOptions()
                .WithAgentUserIdentity(settings.AgentIdentityClientId, Guid.Parse(settings.AgentUserObjectId));
            options.AcquireTokenOptions.AuthenticationOptionsName = AuthenticationOptionsName;
            options.AcquireTokenOptions.Tenant = settings.TenantId;
            string header = await scope.ServiceProvider.GetRequiredService<IAuthorizationHeaderProvider>()
                .CreateAuthorizationHeaderForUserAsync(HumanSettings.Scopes, options, agentUser, timeout.Token);
            if (!AuthenticationHeaderValue.TryParse(header, out var bearer) ||
                !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(bearer.Parameter))
                throw new LabException("Configured AU did not return a WorkIQ bearer. No human/app-only fallback.");
            ValidateTokenShape(bearer.Parameter, settings);
            return bearer.Parameter;
        }
        catch (Exception error) when (error is LabException or MsalException or MicrosoftIdentityWebChallengeUserException or HttpRequestException)
        { throw new AuWorkIqAuthenticationException(); }
        catch (OperationCanceledException) { DiagnoseCancellation(); throw; }
        finally { gate.Release(); }
    }
    internal static void ValidateTokenShape(string token, LabSettings settings)
    {
        JsonWebTokenHandler reader = new();
        if (!reader.CanReadToken(token) || token.Count(c => c == '.') != 2)
            throw new LabException("AU WorkIQ token is not an inspectable signed JWT.");
        JsonWebToken jwt;
        try { jwt = reader.ReadJsonWebToken(token); }
        catch (ArgumentException) { throw new LabException("Malformed AU WorkIQ JWT."); }
        ClaimsPrincipal claims = new(new ClaimsIdentity(jwt.Claims));
        string? Claim(string name) => claims.FindFirst(name)?.Value;
        if (!LabSettings.SameGuid(Claim("tid"), settings.TenantId) ||
            !LabSettings.SameGuid(Claim("oid"), settings.AgentUserObjectId) || !claims.IsAgentUserIdentity() ||
            !jwt.Audiences.Any(a => a is HumanSettings.Resource or HumanSettings.ResourceAppId) ||
            !(Claim("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("WorkIQAgent.Ask") ||
            jwt.ValidTo <= DateTime.UtcNow ||
            (Claim("azp") is string azp && !LabSettings.SameGuid(azp, settings.AgentIdentityClientId)) ||
            (Claim("appid") is string appid && !LabSettings.SameGuid(appid, settings.AgentIdentityClientId)) ||
            (Claim("xms_par_app_azp") is string parent && !LabSettings.SameGuid(parent, settings.BlueprintClientId)))
            throw new LabException("AU WorkIQ token identity/audience/scope/expiry mismatch. No human/app-only fallback.");
    }
    internal static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MicrosoftIdentityApplicationOptions>(AuthenticationOptionsName, options =>
        {
            configuration.GetSection(AuthenticationOptionsName).Bind(options);
            options.Authority = string.Empty;
        });
        services.AddSingleton<AgentUserTokenProvider>();
        services.AddSingleton(sp => new WorkIqSession(sp.GetRequiredService<AgentUserTokenProvider>(),
            new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }));
    }
}
