using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace WorkIqFiles;

internal static class BrowserSignIn
{
    internal const string FlowProperty = "workiq-human-flow";
    private const string CallbackLeaseKey = "workiq-human-callback-lease";
    internal static string? PromptUrl(string response, HumanSettings settings)
    {
        if (!response.StartsWith("Sign in to WorkIQ as the same human account", StringComparison.Ordinal)) return null;
        string[] lines = response.Split('\n');
        if (lines.Length != 3 || !Uri.TryCreate(lines[1], UriKind.Absolute, out Uri? uri) ||
            uri.GetLeftPart(UriPartial.Authority) != settings.PublicBaseUrl.GetLeftPart(UriPartial.Authority) ||
            uri.AbsolutePath != HumanSettings.StartPath || uri.Fragment.Length != 0 ||
            !uri.Query.StartsWith("?ticket=", StringComparison.Ordinal) ||
            uri.Query.Length != 56 || uri.Query[8..].Any(c => !char.IsAsciiHexDigit(c)))
            return null;
        return uri.AbsoluteUri;
    }
    internal static void Register(IServiceCollection services, IConfiguration configuration, HumanSettings settings)
    {
        services.AddSingleton(settings);
        // A named human scheme/cache; neither default authentication nor Teams AzureAd options are replaced.
        services.AddAuthentication().AddMicrosoftIdentityWebApp(configuration.GetSection("HumanWorkIQ"),
            openIdConnectScheme: HumanSettings.Scheme, cookieScheme: HumanSettings.Cookies)
            .EnableTokenAcquisitionToCallDownstreamApi(HumanSettings.Scopes).AddInMemoryTokenCaches();
        services.Configure<ConfidentialClientApplicationOptions>(HumanSettings.Scheme, options =>
            options.RedirectUri = settings.RedirectUri);
        services.Configure<OpenIdConnectOptions>(HumanSettings.Scheme, (OpenIdConnectOptions options) =>
        {
            options.MapInboundClaims = false;
            options.UsePkce = true;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.FormPost;
            options.SaveTokens = false;
            options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(5);
            options.ProtocolValidator.RequireNonce = true;
            options.ProtocolValidator.NonceLifetime = TimeSpan.FromMinutes(5);
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidIssuer = settings.Issuer;
            options.TokenValidationParameters.IssuerValidator = null;
            options.TokenValidationParameters.ValidateAudience = true;
            options.TokenValidationParameters.ValidAudience = settings.Client;
            options.TokenValidationParameters.ValidateLifetime = true;
            options.TokenValidationParameters.RequireSignedTokens = true;
            options.TokenValidationParameters.RequireExpirationTime = true;
            options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.CorrelationCookie.HttpOnly = true;
            options.CorrelationCookie.SameSite = SameSiteMode.None;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.NonceCookie.HttpOnly = true;
            options.NonceCookie.SameSite = SameSiteMode.None;
            var previousRedirect = options.Events.OnRedirectToIdentityProvider;
            options.Events.OnRedirectToIdentityProvider = async context =>
            {
                await previousRedirect(context);
                context.ProtocolMessage.RedirectUri = settings.RedirectUri;
                context.ProtocolMessage.Prompt = "select_account";
                ProtectResponse(context.HttpContext);
            };
            var previousCode = options.Events.OnAuthorizationCodeReceived;
            options.Events.OnAuthorizationCodeReceived = async context =>
            {
                string? flow = null;
                context.Properties?.Items.TryGetValue(FlowProperty, out flow);
                if (flow is null) throw new SecurityTokenValidationException("Missing human sign-in flow.");
                context.HttpContext.Items[CallbackLeaseKey] = await context.HttpContext.RequestServices
                    .GetRequiredService<HumanConnections>().BeginCallback(flow, context.HttpContext.RequestAborted);
                await previousCode(context);
            };
            var previousValidated = options.Events.OnTokenValidated;
            options.Events.OnTokenValidated = async context =>
            {
                // MSAL handles code redemption, so ASP.NET skips its normal token-response nonce
                // validation and permits unsigned code-flow ID tokens. Require both explicitly.
                if (context.TokenEndpointResponse?.IdToken is not string idToken || context.Options.ConfigurationManager is null)
                    throw new SecurityTokenValidationException("Missing signed code-flow ID token.");
                OpenIdConnectConfiguration metadata = await context.Options.ConfigurationManager.GetConfigurationAsync(context.HttpContext.RequestAborted);
                TokenValidationParameters parameters = context.Options.TokenValidationParameters.Clone();
                parameters.RequireSignedTokens = true;
                parameters.ValidateIssuerSigningKey = true;
                parameters.IssuerSigningKeys = metadata.SigningKeys;
                parameters.ValidIssuer = settings.Issuer;
                parameters.IssuerValidator = null;
                parameters.ValidAudience = settings.Client;
                TokenValidationResult validated = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, parameters);
                if (!validated.IsValid) throw new SecurityTokenValidationException("Signed ID-token validation failed.");
                await previousValidated(context);
                if (context.Principal is null || context.HttpContext.Items[CallbackLeaseKey] is not HumanCallbackLease lease)
                    throw new SecurityTokenValidationException("Missing human callback lease.");
                lease.ObserveValidatedPrincipal(context.Principal);
                context.Options.ProtocolValidator.ValidateTokenResponse(new OpenIdConnectProtocolValidationContext
                {
                    ClientId = settings.Client, ProtocolMessage = context.TokenEndpointResponse,
                    ValidatedIdToken = context.SecurityToken, Nonce = context.Nonce
                });
            };
            var previousTicket = options.Events.OnTicketReceived;
            options.Events.OnTicketReceived = async context =>
            {
                await previousTicket(context);
                context.HandleResponse(); // No human cookie/principal becomes ambient on Teams requests.
                ProtectResponse(context.HttpContext);
                try
                {
                    string? flow = null;
                    context.Properties?.Items.TryGetValue(FlowProperty, out flow);
                    if (context.Principal is null || flow is null)
                        throw new LabException("Missing sign-in correlation.");
                    if (context.HttpContext.Items[CallbackLeaseKey] is not HumanCallbackLease lease)
                        throw new LabException("Missing callback lease.");
                    await lease.Complete(context.Principal, context.HttpContext.RequestAborted);
                    // Release the human gate and commit any purpose-bound continuation before reporting success.
                    await lease.DisposeAsync();
                    await context.Response.WriteAsync(lease.PrivateContinuation
                        ? "Human WorkIQ connected. Return to the established personal chat to check work under your separately approved job scope. No operation ran inside this sign-in callback and no result is posted to the channel."
                        : "Human WorkIQ connected. Return to Teams and resend your command. No file operation ran during sign-in.");
                }
                catch (Exception error) when (error is LabException or OperationCanceledException)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Sign-in did not match the initiating Teams human, expired, or lacked a valid WorkIQ grant. Request /signin again in the approved personal chat.");
                }
            };
            options.Events.OnRemoteFailure = async context =>
            {
                context.HandleResponse();
                ProtectResponse(context.HttpContext);
                string? flow = null;
                context.Properties?.Items.TryGetValue(FlowProperty, out flow);
                context.HttpContext.RequestServices.GetRequiredService<HumanConnections>().Abandon(flow);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Sign-in failed or was cancelled. No file operation ran. Request a new /signin link in the approved personal chat.");
            };
        });
        services.AddSingleton<IHumanTokenCache, HumanTokenCache>();
        services.AddSingleton(new WorkIqSessionFactory(tokens =>
            new WorkIqSession(tokens, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })));
        services.AddSingleton<HumanConnections>();
    }
    internal static void UseCallbackCleanup(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            finally
            {
                if (context.Items[CallbackLeaseKey] is HumanCallbackLease lease) await lease.DisposeAsync();
            }
        });
    }
    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(HumanSettings.StartPath, async (HttpContext context, HumanConnections connections) =>
        {
            ProtectResponse(context);
            string ticket = context.Request.Query["ticket"].ToString();
            if (ticket.Length != 48 || ticket.Any(c => !char.IsAsciiHexDigit(c)))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Invalid sign-in link.");
                return;
            }
            try
            {
                string flow = connections.Begin(ticket);
                AuthenticationProperties properties = new();
                properties.Items[FlowProperty] = flow;
                await context.ChallengeAsync(HumanSettings.Scheme, properties);
            }
            catch (LabException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Expired or used sign-in link. Request /signin again in your personal chat.");
            }
        }).AllowAnonymous();
    }
    private static void ProtectResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
}
