using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WorkIqNaturalLanguage;

// Gets a WorkIQ token for the configured agent user (its own identity, no human sign-in, no app-only token).
internal sealed class AgentUserToken(IServiceProvider services, IConfiguration config)
{
    private const string OptionsName = "WorkIQAgent:Blueprint";

    internal static void Register(IServiceCollection services, IConfiguration config)
    {
        services.Configure<MicrosoftIdentityApplicationOptions>(OptionsName, options =>
        {
            config.GetSection(OptionsName).Bind(options);
            options.Authority = string.Empty;
        });
        services.AddSingleton<AgentUserToken>();
    }

    internal async Task<string> AuthorizationHeader(CancellationToken ct)
    {
        using IServiceScope scope = services.CreateScope();
        AuthorizationHeaderProviderOptions options = new AuthorizationHeaderProviderOptions()
            .WithAgentUserIdentity(config["WorkIQAgent:AgentIdentityClientId"]!, Guid.Parse(config["WorkIQAgent:AgentUserObjectId"]!));
        options.AcquireTokenOptions.AuthenticationOptionsName = OptionsName;
        options.AcquireTokenOptions.Tenant = config["WorkIQAgent:TenantId"];
        return await scope.ServiceProvider.GetRequiredService<IAuthorizationHeaderProvider>()
            .CreateAuthorizationHeaderForUserAsync([config["WorkIQ:Scope"]!], options, new ClaimsPrincipal(), ct);
    }
}

// One MCP session to WorkIQ, created on first use. Every HTTP request carries the agent user's token.
internal sealed class WorkIqConnection(Uri endpoint, Func<CancellationToken, Task<string>> authorizationHeader)
{
    private readonly HttpClient http = new(new BearerHandler(authorizationHeader)) { Timeout = TimeSpan.FromMinutes(3) };
    private readonly object sync = new();
    private Task<McpClient>? client;
    private IReadOnlyList<Tool>? catalog;

    private Task<McpClient> Client()
    {
        lock (sync)
        {
            if (client is null || client.IsFaulted || client.IsCanceled)
                client = McpClient.CreateAsync(new HttpClientTransport(
                    new HttpClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp }, http));
            return client;
        }
    }

    internal async Task<IReadOnlyList<Tool>> Catalog(CancellationToken ct)
    {
        if (catalog is not null) return catalog;
        McpClient c = await Client();
        List<Tool> tools = [];
        string? cursor = null;
        do
        {
            ListToolsResult page = await c.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, ct);
            tools.AddRange(page.Tools ?? []);
            cursor = page.NextCursor;
        } while (!string.IsNullOrEmpty(cursor));
        return catalog = tools;
    }

    // Arguments are forwarded exactly as the model wrote them.
    internal async Task<CallToolResult> Call(string tool, JsonElement arguments, CancellationToken ct)
    {
        Dictionary<string, object?> values = arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
            : [];
        return await (await Client()).CallToolAsync(tool, values, cancellationToken: ct);
    }

    private sealed class BearerHandler(Func<CancellationToken, Task<string>> authorizationHeader)
        : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(await authorizationHeader(ct));
            return await base.SendAsync(request, ct);
        }
    }
}
