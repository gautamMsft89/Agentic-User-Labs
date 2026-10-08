using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Teams.Apps;
using WorkIqFiles;

internal sealed record OidcChallenge(string Ticket, Dictionary<string, string> Query, string Cookie);
internal sealed class OidcFixture : IAsyncDisposable
{
    private readonly TestServer server;
    private readonly IHost host;
    private readonly Harness data;
    internal readonly HumanSettings Settings = new(Harness.Config, new(Harness.Config));
    internal readonly SyntheticEntra Entra = new();
    internal readonly McpHandler Mcp = new();
    internal readonly McpHandler AuMcp = new();
    internal readonly Dictionary<string, McpHandler> HumanMcps = [];
    internal readonly HttpClient Client;
    internal ClaimsPrincipal? LastPrincipal;
    internal bool CodeHandled;
    internal IServiceProvider Services => server.Services;
    internal HumanConnections Connections => Services.GetRequiredService<HumanConnections>();
    internal WorkIqIngress Agent => Services.GetRequiredService<WorkIqIngress>();

    internal OidcFixture(bool humanEnabled = true)
    {
        data = new() { AutoConnect = false };
        host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureAppConfiguration((_, configuration) => configuration.AddConfiguration(Harness.Config))
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                services.AddTeamsBotApplication();
                services.AddSingleton(data.Settings); services.AddSingleton(data.Policy);
                services.AddSingleton(new SetupMode());
                services.AddSingleton<TimeProvider>(data.Clock);
                AgentUserTokenProvider.Register(services, Harness.Config);
                services.Replace(ServiceDescriptor.Singleton(sp => new WorkIqSession(sp.GetRequiredService<AgentUserTokenProvider>(), AuMcp)));
                if (humanEnabled) BrowserSignIn.Register(services, Harness.Config, Settings);
                services.Replace(ServiceDescriptor.Singleton(new WorkIqSessionFactory(tokens =>
                {
                    string user = tokens is HumanTokens bound ? bound.Key.User : throw new Exception("Missing human binding.");
                    McpHandler handler = user == Harness.Human ? Mcp : new();
                    HumanMcps[user] = handler;
                    return new(tokens, handler);
                })));
                services.AddSingleton<IMsalHttpClientFactory>(new FakeMsal(new HttpClient(Entra)));
                services.AddSingleton(sp => new WorkIqRouter(sp.GetRequiredService<WorkIqSession>(),
                    sp.GetService<HumanConnections>(), humanEnabled ? Settings : null, data.Settings));
                services.AddSingleton<ChannelSetup>();
                services.AddSingleton(new NaturalLanguageOptions());
                services.AddSingleton<IConversationAgent, NaturalLanguageAgent>();
                services.AddSingleton<WorkIqIngress>();
                services.PostConfigure<OpenIdConnectOptions>(HumanSettings.Scheme, options =>
                {
                    OpenIdConnectConfiguration configuration = new()
                    {
                        Issuer = Settings.Issuer,
                        AuthorizationEndpoint = Settings.Issuer + "/authorize",
                        TokenEndpoint = $"https://login.microsoftonline.com/{Harness.Tenant}/oauth2/v2.0/token"
                    };
                    configuration.SigningKeys.Add(Entra.Key);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    options.Backchannel = new HttpClient(Entra);
                    var codeReceived = options.Events.OnAuthorizationCodeReceived;
                    options.Events.OnAuthorizationCodeReceived = async context =>
                    {
                        await codeReceived(context);
                        CodeHandled = context.HandledCodeRedemption;
                    };
                    var validated = options.Events.OnTokenValidated;
                    options.Events.OnTokenValidated = async context =>
                    {
                        await validated(context);
                        LastPrincipal = context.Principal;
                    };
                });
            })
            .Configure(app =>
            {
                app.UseRouting();
                BrowserSignIn.UseCallbackCleanup(app);
                app.UseAuthentication(); app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    if (humanEnabled) BrowserSignIn.Map(endpoints);
                    endpoints.MapGet("/validation/ambient", (HttpContext context) =>
                        context.User.Identity?.IsAuthenticated == true ? "authenticated" : "anonymous");
                });
            })).Build();
        host.Start();
        server = host.GetTestServer();
        Client = server.CreateClient();
        Client.BaseAddress = Settings.PublicBaseUrl;
    }
    internal async Task<OidcChallenge> Begin(string human = Harness.Human)
    {
        string prompt = (await Agent.Handle(Harness.Activity("/signin", "personal", human), CancellationToken.None))!;
        string ticket = Harness.Ticket(prompt);
        HttpResponseMessage response = await Client.GetAsync(HumanSettings.StartPath + "?ticket=" + ticket);
        if (response.StatusCode != HttpStatusCode.Redirect) throw new Exception("OIDC challenge did not redirect: " + response.StatusCode);
        Dictionary<string, string> query = QueryHelpers.ParseQuery(response.Headers.Location!.Query)
            .ToDictionary(p => p.Key, p => p.Value.ToString());
        string cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        Entra.Nonce = query["nonce"]; Entra.Challenge = query["code_challenge"]; Entra.User = human;
        return new(ticket, query, cookie);
    }
    internal async Task<HttpResponseMessage> Complete(OidcChallenge challenge, bool corruptState = false, bool omitCookie = false)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, HumanSettings.CallbackPath);
        if (!omitCookie) request.Headers.Add("Cookie", challenge.Cookie);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["state"] = corruptState ? "tampered-state" : challenge.Query["state"], ["code"] = "synthetic-one-use-code",
            ["client_info"] = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                uid = Entra.Failure == "user" ? Harness.Other : Entra.User,
                utid = Entra.Failure == "tenant" ? Harness.Other : Harness.Tenant
            }))
        });
        return await Client.SendAsync(request);
    }
    public async ValueTask DisposeAsync()
    {
        if (Services.GetService<HumanConnections>() is HumanConnections connections) await connections.DisposeAsync();
        Client.Dispose(); await host.StopAsync(); host.Dispose(); Entra.Dispose();
        await data.DisposeAsync();
    }
    private sealed class FakeMsal(HttpClient http) : IMsalHttpClientFactory
    { public HttpClient GetHttpClient() => http; }
}

// Real signed synthetic ID tokens and real MSAL code/refresh processing. All HTTP ends here.
internal sealed class SyntheticEntra : HttpMessageHandler
{
    private readonly RSA rsa = RSA.Create(2048);
    internal readonly RsaSecurityKey Key;
    internal string Nonce = "", Challenge = "", User = Harness.Human, Failure = "";
    internal bool PkceVerified, ShortFirstToken;
    internal int CodeRequests, RefreshRequests;
    internal int AuRequests;
    internal string HumanOAuthClient = Harness.HumanClient;
    internal string HumanScope = HumanSettings.Scopes[0];
    internal string HumanCallback = "https://files.example.invalid/signin-workiq-files";
    internal SyntheticEntra() => Key = new(rsa) { KeyId = "synthetic-oidc-key" };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Uri uri = request.RequestUri!;
        if (uri.Host != "login.microsoftonline.com") throw new Exception("Unexpected synthetic auth egress.");
        string tokenEndpoint = $"https://login.microsoftonline.com/{Harness.Tenant}/oauth2/v2.0/token";
        if (request.Method == HttpMethod.Get && uri.AbsolutePath.Contains("/discovery/instance"))
            return Json(new
            {
                tenant_discovery_endpoint = $"https://login.microsoftonline.com/{Harness.Tenant}/v2.0/.well-known/openid-configuration",
                metadata = new[] { new { preferred_network = "login.microsoftonline.com", preferred_cache = "login.microsoftonline.com",
                    aliases = new[] { "login.microsoftonline.com", "login.windows.net", "sts.windows.net" } } }
            });
        if (request.Method == HttpMethod.Get && uri.AbsolutePath.EndsWith("/.well-known/openid-configuration"))
            return Json(new
            {
                issuer = $"https://login.microsoftonline.com/{Harness.Tenant}/v2.0",
                authorization_endpoint = $"https://login.microsoftonline.com/{Harness.Tenant}/oauth2/v2.0/authorize",
                token_endpoint = tokenEndpoint, jwks_uri = $"https://login.microsoftonline.com/{Harness.Tenant}/discovery/v2.0/keys",
                response_types_supported = new[] { "code", "id_token" }, subject_types_supported = new[] { "pairwise" },
                id_token_signing_alg_values_supported = new[] { "RS256" }
            });
        if (request.Method != HttpMethod.Post || uri.AbsoluteUri != tokenEndpoint) throw new Exception("Unexpected OAuth endpoint.");
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
        string Value(string key) => form.TryGetValue(key, out var value) ? value.ToString() : "";
        string client = Value("client_id");
        string grant = Value("grant_type");
        if (client == Harness.Blueprint && grant == "client_credentials")
        {
            if (Value("client_secret") != "synthetic-au-blueprint-secret" ||
                Value("scope") != "api://AzureAdTokenExchange/.default" || Value("fmi_path") != Harness.Agent)
                throw new Exception("AU blueprint flow mixed with human credentials.");
            return Json(new { token_type = "Bearer", expires_in = 3600, access_token = "synthetic-au-t1" });
        }
        if (client == Harness.Agent && grant == "client_credentials")
        {
            if (Value("client_assertion") != "synthetic-au-t1") throw new Exception("AU agent assertion mismatch.");
            return Json(new { token_type = "Bearer", expires_in = 3600, access_token = "synthetic-au-t2" });
        }
        if (client == Harness.Agent && grant == "user_fic")
        {
            if (Value("user_id") != Harness.Au || Value("client_assertion") != "synthetic-au-t1" ||
                Value("user_federated_identity_credential") != "synthetic-au-t2" || !Value("scope").Contains(HumanSettings.Scopes[0]))
                throw new Exception("AU grant mixed with human identity.");
            AuRequests++;
            return Json(new { token_type = "Bearer", expires_in = 3600, access_token = SyntheticToken.CreateAu(),
                id_token = SyntheticToken.CreateAu(), scope = HumanSettings.Scopes[0],
                client_info = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { uid = Harness.Au, utid = Harness.Tenant })) });
        }
        if (client != HumanOAuthClient || Value("client_secret") != "synthetic-human-secret")
            throw new Exception("Human flow used wrong client/secret.");
        if (grant == "authorization_code")
        {
            CodeRequests++;
            string verifier = Value("code_verifier");
            PkceVerified = verifier.Length >= 43 && WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) == Challenge;
            if (!PkceVerified || Value("redirect_uri") != HumanCallback)
                throw new Exception("PKCE or fixed callback mismatch.");
        }
        else if (grant == "refresh_token")
        {
            RefreshRequests++;
            if (Value("refresh_token") != "synthetic-refresh-" + User) throw new Exception("Cross-human refresh.");
        }
        else throw new Exception("Forbidden OAuth flow: " + grant);
        string[] scopes = Value("scope").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] allowedScopes = [HumanScope];
        if (!allowedScopes.All(scopes.Contains) ||
            scopes.Any(s => !allowedScopes.Contains(s) && s is not ("openid" or "profile" or "offline_access")))
            throw new Exception("Wrong WorkIQ human delegated scopes.");
        string tenant = Failure == "tenant" ? Harness.Other : Harness.Tenant;
        string user = Failure == "user" ? Harness.Other : User;
        string issuer = Failure == "issuer" ? "https://evil.invalid/" : $"https://login.microsoftonline.com/{Harness.Tenant}/v2.0";
        Dictionary<string, object> claims = new()
        {
            ["tid"] = tenant, ["oid"] = user, ["sub"] = user, ["name"] = "Synthetic human",
            ["preferred_username"] = "synthetic@example.invalid", ["nonce"] = Failure == "nonce" ? "wrong-nonce" : Nonce
        };
        DateTime expires = Failure == "expiry" ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddHours(1);
        if (Failure == "missing-nonce") claims.Remove("nonce");
        string idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = Failure == "audience" ? Harness.Agent : HumanOAuthClient,
            Claims = claims, IssuedAt = DateTime.UtcNow.AddHours(-1), NotBefore = DateTime.UtcNow.AddHours(-1),
            Expires = expires, SigningCredentials = Failure == "unsigned" ? null : new(Key, SecurityAlgorithms.RsaSha256)
        });
        if (Failure == "signature") idToken = idToken[..(idToken.LastIndexOf('.') + 1)] + WebEncoders.Base64UrlEncode(new byte[256]);
        return Json(new
        {
            token_type = "Bearer", expires_in = ShortFirstToken && grant == "authorization_code" ? 1 : 3600,
            access_token = SyntheticToken.Create(user, "jti", "generation-" + RefreshRequests),
            id_token = idToken, refresh_token = "synthetic-refresh-" + User,
            scope = Failure == "wrong-scope" ? "https://graph.microsoft.com/User.Read" : string.Join(" ", allowedScopes),
            client_info = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { uid = user, utid = tenant }))
        });
    }
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    protected override void Dispose(bool disposing) { if (disposing) rsa.Dispose(); base.Dispose(disposing); }
}
