using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.Identity.Abstractions;
using WorkIqFiles;

internal static class HumanChecks
{
    internal static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("human token guard accepts expected resource and rejects AU/Graph/app-only/mixups", async () =>
        {
            await using Harness h = new();
            Invocation invocation = h.Policy.Authorize(Harness.Activity("/status"), h.Settings);
            HumanKey key = h.HumanSettings.Key(invocation);
            h.HumanSettings.ValidateAccessToken(SyntheticToken.Create(Harness.Human), key, Harness.Principal(Harness.Human));
            foreach ((string claim, object? value) in new (string, object?)[]
            {
                ("oid", Harness.Other), ("oid", Harness.Au), ("tid", Harness.Other),
                ("aud", "https://graph.microsoft.com"), ("aud", "https://botapi.skype.com"), ("scp", null),
                ("scp", "Files.ReadWrite.All"), ("xms_sub_fct", "13"), ("azp", Harness.Agent),
                ("appid", Harness.Other), ("exp", 1L), ("iss", "https://evil.invalid/")
            })
                await Denied<HumanSignInRequiredException>(() =>
                {
                    h.HumanSettings.ValidateAccessToken(SyntheticToken.Create(Harness.Human, claim, value), key, Harness.Principal(Harness.Human));
                    return Task.CompletedTask;
                });
        });
        await check("private signin prerequisite, shared channel guidance has no link", async () =>
        {
            await using Harness h = new() { AutoConnect = false };
            Has(await NativeAuthFixture.Read(h, "human-drive", "human-file"), "Human WorkIQ sign-in required");
            string? channel = await h.Send("/signin");
            Has(channel, "personal chat"); Must(!channel!.Contains("https://"));
            string? personal = await h.Send("/signin", "personal");
            Has(personal, "https://files.example.invalid/auth/files/signin?ticket=");
            Must(h.Handler.ToolCalls == 0);
        });
        await check("one-use signin link, wrong user/tenant and expired flow deny", async () =>
        {
            await using Harness h = new() { AutoConnect = false };
            foreach (string mismatch in new[] { "user", "tenant", "expiry" })
            {
                string ticket = Harness.Ticket((await h.Send("/signin", "personal"))!);
                string flow = h.Connections.Begin(ticket);
                await Denied<LabException>(() => { h.Connections.Begin(ticket); return Task.CompletedTask; });
                if (mismatch == "expiry") h.Clock.Now = h.Clock.Now.AddMinutes(6);
                await Denied<LabException>(() => h.Connections.Complete(flow,
                    Harness.Principal(mismatch == "user" ? Harness.Other : Harness.Human,
                        mismatch == "tenant" ? Harness.Other : Harness.Tenant), CancellationToken.None));
                Has(await h.Send("/status"), "disconnected");
                Must(h.Cache.Acquired.Count == 0);
            }
        });
        await check("personal sign-in usable only by SAME human in allowlisted channel", async () =>
        {
            await using Harness h = new() { AutoConnect = false };
            await h.Connect(Harness.Human);
            Has(await NativeAuthFixture.Read(h, "human-drive", "human-file"), "personal.txt");
            Has(await NativeAuthFixture.Read(h, "other-drive", "other-file", Harness.Other), "sign-in required");
            Must(h.HumanHandlers.Count == 1 && h.Handler.Bearers.All(t => new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(t).GetClaim("oid").Value == Harness.Human));
        });
        await check("two humans interleave with isolated bearer/session/cache", async () =>
        {
            await using Harness h = new() { AutoConnect = false };
            await h.Connect(Harness.Human); await h.Connect(Harness.Other);
            h.Handler.DelayMs = 5;
            string?[] results = await Task.WhenAll(
                NativeAuthFixture.Read(h, "human-drive", "human-file", Harness.Human),
                NativeAuthFixture.Read(h, "other-drive", "other-file", Harness.Other),
                NativeAuthFixture.Read(h, "human-drive", "human-root"));
            Must(results.All(r => r!.Contains("name=")));
            Must(h.HumanHandlers.Count == 2 && h.HumanHandlers.Values.All(handler => handler.Initializes == 1));
            foreach (var pair in h.HumanHandlers)
                Must(pair.Value.Bearers.All(t => new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(t).GetClaim("oid").Value == pair.Key));
            Has(await h.Send("/disconnect"), "disconnected");
            Has(await h.Send("/status", user: Harness.Other), "connected to this requester");
            Must(h.Cache.Removed.SequenceEqual([Harness.Human]));
            Has(await NativeAuthFixture.Read(h, "other-drive", "other-file", Harness.Other), "other.txt");
        });
        await check("disconnect invalidates inflight login and old human session without preview state", async () =>
        {
            await using Harness h = new() { AutoConnect = false };
            string oldFlow = h.Connections.Begin(Harness.Ticket((await h.Send("/signin", "personal"))!));
            await h.Send("/disconnect");
            await Denied<LabException>(() => h.Connections.Complete(oldFlow, Harness.Principal(Harness.Human), CancellationToken.None));
            await h.Connect(Harness.Human);
            await h.Send("/disconnect");
            Has(await NativeAuthFixture.Read(h, "human-drive", "human-file"), "sign-in required");
            Must(h.Handler.Mutations == 0);
        });
        await check("renewed bearer checked each request; native refresh mixup denies and explicit disconnect clears only that human", async () =>
        {
            await using Harness h = new() { AutoConnect = false };
            await h.Connect(Harness.Human); await h.Connect(Harness.Other);
            Has(await NativeAuthFixture.Read(h, "human-drive", "human-file"), "personal.txt");
            h.Cache.Tokens[Harness.Human] = SyntheticToken.Create(Harness.Human, "jti", "renewed");
            Has(await NativeAuthFixture.Read(h, "human-drive", "human-file"), "personal.txt");
            Must(h.Handler.Bearers.Last() == h.Cache.Tokens[Harness.Human]);
            h.Cache.Tokens[Harness.Human] = SyntheticToken.Create(Harness.Other);
            Has(await NativeAuthFixture.Read(h, "human-drive", "human-file"), "sign-in required");
            await h.Send("/disconnect");
            Has(await h.Send("/status"), "disconnected");
            Has(await NativeAuthFixture.Read(h, "other-drive", "other-file", Harness.Other), "other.txt");
        });
        await check("revoked consent requires explicit new signin and has no fallback", async () =>
        {
            await using Harness h = new() { AutoConnect = false };
            await h.Connect(Harness.Human);
            h.Cache.Deny = true;
            Has(await NativeAuthFixture.Read(h, "human-drive", "human-file"), "sign-in required");
            await h.Send("/disconnect");
            Has(await h.Send("/status"), "disconnected");
            Must(h.Handler.ToolCalls == 0);
        });

        await check("real OIDC+MIW middleware authorization-code PKCE and named Teams isolation", async () =>
        {
            await using OidcFixture f = new();
            OidcChallenge challenge = await f.Begin();
            Must(challenge.Query["code_challenge_method"] == "S256");
            Must(challenge.Query["response_type"] == "code" && challenge.Query["response_mode"] == "form_post");
            Must(challenge.Query["redirect_uri"] == f.Settings.RedirectUri);
            Must(challenge.Query["scope"].Contains(HumanSettings.Scopes[0]));
            Must(!challenge.Query["scope"].Contains("graph.microsoft.com"));
            HttpResponseMessage result = await f.Complete(challenge);
            Must(result.StatusCode == HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
            Has(await NativeAuthFixture.Read(f, "human-drive", "human-file"), "personal.txt");
            Must(f.Entra.CodeRequests == 1 && f.Entra.PkceVerified && f.Mcp.Bearers.Count > 0);
            foreach (string bearer in f.Mcp.Bearers)
                Must(new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(bearer).GetClaim("oid").Value == Harness.Human);
            var named = f.Services.GetRequiredService<IOptionsMonitor<MicrosoftIdentityApplicationOptions>>();
            Must(named.Get("AzureAd").ClientId == Harness.Blueprint);
            Must(named.Get("AzureAd").ClientCredentials!.Single().ClientSecret == "synthetic-transport-secret");
            AuthenticationOptions defaults = f.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
            Must(defaults.DefaultAuthenticateScheme != HumanSettings.Scheme && defaults.DefaultScheme != HumanSettings.Cookies);
            OpenIdConnectOptions options = f.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(HumanSettings.Scheme);
            Must(options.UsePkce && !options.SaveTokens && options.ProtocolValidator.RequireNonce &&
                options.TokenValidationParameters.ValidateIssuer && options.TokenValidationParameters.ValidateLifetime);
            string ambient = await f.Client.GetStringAsync("/validation/ambient");
            Must(ambient == "anonymous");
            Must(!result.Headers.TryGetValues("Set-Cookie", out var cookies) || cookies.All(c => !c.Contains(".AspNetCore.HumanWorkIQCookies")));
            await Denied<LabException>(() => { f.Connections.Begin(challenge.Ticket); return Task.CompletedTask; });
        });
        foreach (string failure in new[] { "nonce", "missing-nonce", "issuer", "audience", "expiry", "signature", "unsigned", "tenant", "user", "state", "correlation" })
            await check("real middleware denies " + failure, async () =>
            {
                await using OidcFixture f = new();
                OidcChallenge challenge = await f.Begin();
                f.Entra.Failure = failure;
                HttpResponseMessage response = await f.Complete(challenge,
                    corruptState: failure == "state", omitCookie: failure == "correlation");
                Must(response.StatusCode == HttpStatusCode.BadRequest, $"Unexpected callback status {response.StatusCode}");
                Has(await f.Agent.Handle(Harness.Activity("/status"), CancellationToken.None), "disconnected");
                Must(f.Mcp.ToolCalls == 0);
            });
        await check("real human cache refresh and per-account disconnect eviction", async () =>
        {
            await using OidcFixture f = new();
            f.Entra.ShortFirstToken = true;
            OidcChallenge challenge = await f.Begin();
            Must((await f.Complete(challenge)).StatusCode == HttpStatusCode.OK);
            Must(f.Entra.RefreshRequests > 0);
            Has(await f.Agent.Handle(Harness.Activity("/disconnect"), CancellationToken.None), "cache");
            Has(await NativeAuthFixture.Read(f, "human-drive", "human-file"), "sign-in required");
            // Test the actual MSAL cache, not just the connection marker.
            IHumanTokenCache tokens = f.Services.GetRequiredService<IHumanTokenCache>();
            await Denied<HumanSignInRequiredException>(() => tokens.Acquire(f.LastPrincipal!, CancellationToken.None));
        });
        await check("actual callback replay and caller-provided redirect cannot authorize another flow", async () =>
        {
            await using OidcFixture f = new();
            OidcChallenge challenge = await f.Begin();
            Must((await f.Complete(challenge)).StatusCode == HttpStatusCode.OK);
            int redeemed = f.Entra.CodeRequests;
            Must((await f.Complete(challenge)).StatusCode == HttpStatusCode.BadRequest);
            Must(f.Entra.CodeRequests == redeemed);
            await f.Agent.Handle(Harness.Activity("/disconnect"), CancellationToken.None);
            string prompt = (await f.Agent.Handle(Harness.Activity("/signin", "personal"), CancellationToken.None))!;
            Must(BrowserSignIn.PromptUrl(prompt, f.Settings) is not null);
            Must(BrowserSignIn.PromptUrl(prompt.Replace(f.Settings.PublicBaseUrl.Host, "untrusted.example"), f.Settings) is null);
            Must(BrowserSignIn.PromptUrl(prompt.Replace("\nNo file", "#fragment\nNo file"), f.Settings) is null);
            using HttpResponseMessage response = await f.Client.GetAsync(HumanSettings.StartPath +
                "?ticket=" + Harness.Ticket(prompt) + "&returnUrl=https%3A%2F%2Funtrusted.example");
            Must(response.StatusCode == HttpStatusCode.Redirect);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            Must(query["redirect_uri"].ToString() == f.Settings.RedirectUri);
        });
        await check("actual AU grant coexists with human auth without sign-in requirement or leakage", async () =>
        {
            await using OidcFixture f = new();
            string? auResult = await NativeAuthFixture.Read(f, "drive", "file");
            Has(auResult, "WorkIQ identity: AU"); Has(auResult, "sample.txt");
            Must(f.Entra.AuRequests >= 1 && f.Entra.CodeRequests == 0);
            Has(await NativeAuthFixture.Read(f, "human-drive", "human-file"), "sign-in required");
            Must(f.Mcp.ToolCalls == 0);
            Must((await f.Complete(await f.Begin())).StatusCode == HttpStatusCode.OK);
            Has(await NativeAuthFixture.Read(f, "human-drive", "human-file"), "WorkIQ identity: requesting user");
            Has(await NativeAuthFixture.Read(f, "drive", "file"), "WorkIQ identity: AU");
            Must(f.AuMcp.Bearers.All(t => Oid(t) == Harness.Au) && f.Mcp.Bearers.All(t => Oid(t) == Harness.Human));
        });
        await check("actual AU plus two human caches/sessions interleaved and one human disconnect", async () =>
        {
            await using OidcFixture f = new();
            Must((await f.Complete(await f.Begin())).StatusCode == HttpStatusCode.OK);
            ClaimsPrincipal alice = f.LastPrincipal!;
            Must((await f.Complete(await f.Begin(Harness.Other))).StatusCode == HttpStatusCode.OK);
            string?[] responses = await Task.WhenAll(
                NativeAuthFixture.Read(f, "drive", "file"),
                NativeAuthFixture.Read(f, "human-drive", "human-file"),
                NativeAuthFixture.Read(f, "other-drive", "other-file", Harness.Other));
            Must(responses.All(r => r!.Contains("name=")));
            Must(f.AuMcp.Bearers.All(t => Oid(t) == Harness.Au));
            foreach (var pair in f.HumanMcps) Must(pair.Value.Bearers.All(t => Oid(t) == pair.Key));
            await f.Agent.Handle(Harness.Activity("/disconnect"), CancellationToken.None);
            await Denied<HumanSignInRequiredException>(() => f.Services.GetRequiredService<IHumanTokenCache>().Acquire(alice, CancellationToken.None));
            Has(await NativeAuthFixture.Read(f, "other-drive", "other-file", Harness.Other), "other.txt");
            Has(await NativeAuthFixture.Read(f, "drive", "file"), "sample.txt");
        });
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
            await check($"HTTP {(int)status} never switches AU or human identity", async () =>
            {
                await using OidcFixture f = new();
                Must((await f.Complete(await f.Begin())).StatusCode == HttpStatusCode.OK);
                f.AuMcp.ToolStatus = status;
                Has(await NativeAuthFixture.Read(f, "drive", "file"), "WorkIQ identity: AU");
                Must(f.Mcp.ToolCalls == 0);
                f.AuMcp.ToolStatus = null;
                f.Mcp.ToolStatus = status;
                int auCalls = f.AuMcp.ToolCalls;
                Has(await NativeAuthFixture.Read(f, "human-drive", "human-file"), "WorkIQ identity: requesting user");
                Must(f.AuMcp.ToolCalls == auCalls);
            });
        await check("AU-only configuration needs no human auth services or secrets", async () =>
        {
            await using OidcFixture f = new(humanEnabled: false);
            Must(f.Services.GetService<HumanConnections>() is null);
            string? response = await NativeAuthFixture.Read(f, "drive", "file");
            Has(response, "WorkIQ identity: AU"); Has(response, "sample.txt");
            Has(await NativeAuthFixture.Read(f, "human-drive", "human-file"), "sign-in required");
            Has(await f.Agent.Handle(Harness.Activity("/signin", "personal"), CancellationToken.None), "unconfigured");
            Must(f.Entra.CodeRequests == 0 && f.Entra.AuRequests > 0);
            Must((await f.Client.GetAsync(HumanSettings.StartPath)).StatusCode == HttpStatusCode.NotFound);
        });




        await check("disconnect waits for callback lease then evicts its resulting human cache", async () =>
        {
            await using Harness h = new() { AutoConnect = false };
            string flow = h.Connections.Begin(Harness.Ticket((await h.Send("/signin", "personal"))!));
            HumanCallbackLease lease = await h.Connections.BeginCallback(flow, CancellationToken.None);
            Task<string?> disconnect = h.Send("/disconnect");
            Must(!disconnect.IsCompleted);
            lease.ObserveValidatedPrincipal(Harness.Principal(Harness.Human));
            await lease.Complete(Harness.Principal(Harness.Human), CancellationToken.None);
            await lease.DisposeAsync();
            Has(await disconnect, "disconnected");
            Must(h.Cache.Removed.Contains(Harness.Human));
            Has(await h.Send("/status"), "disconnected");
        });
        await check("AU claims reject human and wrong audience without alternate flow", async () =>
        {
            await using Harness h = new();
            AgentUserTokenProvider.ValidateTokenShape(SyntheticToken.CreateAu(), h.Settings);
            foreach ((string key, object? value) in new (string, object?)[] { ("oid", Harness.Human),
                ("tid", Harness.Other), ("aud", "https://graph.microsoft.com"), ("scp", null),
                ("xms_sub_fct", null), ("azp", Harness.HumanClient), ("xms_par_app_azp", Harness.Other), ("exp", 1L) })
                await Denied<LabException>(() => { AgentUserTokenProvider.ValidateTokenShape(SyntheticToken.CreateAu(key, value), h.Settings); return Task.CompletedTask; });
        });
    }
    private static string Oid(string token) => new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).GetClaim("oid").Value;
    private static void Must(bool condition, string message = "Human auth assertion failed") { if (!condition) throw new Exception(message); }
    private static void Has(string? text, string expected) => Must(text?.Contains(expected, StringComparison.Ordinal) == true, $"Expected '{expected}'; got '{text}'.");
    private static async Task Denied<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
