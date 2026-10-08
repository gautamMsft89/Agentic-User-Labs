using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Claims;
using Microsoft.Teams.Apps.Schema;
using WorkIqFiles;

internal sealed class Clock : TimeProvider
{
    internal DateTimeOffset Now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class Tokens(string token) : IWorkIqTokenProvider
{
    internal string Token = token;
    internal int Calls;
    public Task<string> GetTokenAsync(CancellationToken cancellationToken)
    { Interlocked.Increment(ref Calls); return Task.FromResult(Token); }
}
internal sealed class Harness : IAsyncDisposable
{
    internal const string Tenant = "11111111-1111-1111-1111-111111111111";
    internal const string Au = "22222222-2222-2222-2222-222222222222";
    internal const string Agent = "33333333-3333-3333-3333-333333333333";
    internal const string Blueprint = "44444444-4444-4444-4444-444444444444";
    internal const string Human = "55555555-5555-5555-5555-555555555555";
    internal const string Other = "66666666-6666-6666-6666-666666666666";
    internal const string Team = "77777777-7777-7777-7777-777777777777";
    internal const string HumanClient = "88888888-8888-8888-8888-888888888888";
    internal const string Channel = "19:channel";
    internal const string Link = "https://synthetic.sharepoint.com/:t:/s/lab/approved?e=synthetic";
    internal static IConfiguration Config => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["AzureAd:ClientId"] = Blueprint, ["AzureAd:TenantId"] = Tenant,
        ["AzureAd:ClientCredentials:0:SourceType"] = "ClientSecret",
        ["AzureAd:ClientCredentials:0:ClientSecret"] = "synthetic-transport-secret",
        ["WorkIQAgent:TenantId"] = Tenant, ["WorkIQAgent:AgentIdentityClientId"] = Agent,
        ["WorkIQAgent:AgentUserObjectId"] = Au, ["WorkIQAgent:Blueprint:ClientId"] = Blueprint,
        ["WorkIQAgent:Blueprint:TenantId"] = Tenant, ["WorkIQAgent:Blueprint:Instance"] = "https://login.microsoftonline.com/",
        ["WorkIQAgent:Blueprint:ClientCredentials:0:SourceType"] = "ClientSecret",
        ["WorkIQAgent:Blueprint:ClientCredentials:0:ClientSecret"] = "synthetic-au-blueprint-secret",
        ["HumanWorkIQ:TenantId"] = Tenant, ["HumanWorkIQ:ClientId"] = HumanClient,
        ["HumanWorkIQ:ClientSecret"] = "synthetic-human-secret",
        ["HumanWorkIQ:Instance"] = "https://login.microsoftonline.com/",
        ["HumanWorkIQ:CallbackPath"] = HumanSettings.CallbackPath,
        ["HumanWorkIQ:PublicBaseUrl"] = "https://files.example.invalid/"
    }).Build();
    internal readonly LabSettings Settings = new(Config);
    internal readonly FilePolicy Policy;
    internal readonly Clock Clock = new();
    internal readonly McpHandler Handler = new();
    internal readonly Tokens Tokens = new(SyntheticToken.Create(Human));
    internal readonly WorkIqSession Session;
    internal readonly WorkIqSession AuSession;
    internal readonly WorkIqRouter Router;
    internal readonly WorkIqIngress App;
    internal readonly HumanSettings HumanSettings;
    internal readonly HumanConnections Connections;
    internal readonly FakeHumanCache Cache = new();
    internal readonly Dictionary<string, McpHandler> HumanHandlers = [];
    private readonly HashSet<string> connected = [];
    internal bool AutoConnect = true;

    internal Harness()
    {
        RootPolicy channelRoot = new() { DriveId = "drive", ItemId = "root", Kind = "channel", TeamId = Team, ChannelId = Channel };
        RootPolicy humanRoot = new() { DriveId = "human-drive", ItemId = "human-root", Kind = "requester", UserId = Human, AllowPersonalDataInSharedContext = true };
        RootPolicy otherRoot = new() { DriveId = "other-drive", ItemId = "other-root", Kind = "requester", UserId = Other, AllowPersonalDataInSharedContext = true };
        ContextPolicy Context(string id, string type) => new()
        {
            ConversationId = id, Type = type, TeamId = type == "channel" ? Team : "", ChannelId = type == "channel" ? Channel : "",
            RequesterIds = [Human, Other], ShareRecipientIds = [Human, Other], Roots = [channelRoot, humanRoot, otherRoot],
            EnableMutations = true
        };
        Policy = new()
        {
            EnableMutations = true,
            Contexts = [Context(Channel, "channel"), Context("19:group", "groupChat"), Context("19:personal", "personal")],
            Links = [new() { ConversationId = Channel, Url = Link, DriveId = "drive", ItemId = "file" }],
            UploadLocations = [new() { DriveId = "drive", FolderId = "root", SiteUrl = "https://synthetic.sharepoint.com/sites/lab",
                SiteKey = "opaque-synthetic-site-key", ServerRelativeUrl = "/sites/lab/Documents/Test" }]
        };
        Policy.Validate();
        Session = new(Tokens, Handler);
        AuSession = new(new GuardedAuTokens(Settings), Handler);
        HumanSettings = new(Config, Settings);
        Connections = new(HumanSettings, Cache, new(provider =>
        {
            string user = provider is WorkIqFiles.HumanTokens bound ? bound.Key.User : throw new Exception("Unbound provider.");
            McpHandler handler = user == Human ? Handler : new();
            HumanHandlers[user] = handler;
            return new(provider, handler);
        }), Clock);
        Router = new(AuSession, Connections, HumanSettings, Settings);
        App = new(Router, Settings, Policy, new NaturalLanguageAgent(new(), Router, Policy, Settings, new(), new()));
    }
    internal async Task<string?> Send(string text, string type = "channel", string user = Human)
    {
        if (AutoConnect && user is Human or Other && !connected.Contains(user))
        {
            await Connect(user);
            connected.Add(user);
        }
        return await App.Handle(Activity(text, type, user), CancellationToken.None);
    }
    internal async Task Connect(string user)
    {
        Invocation invocation = Policy.Authorize(Activity("/signin", "personal", user), Settings);
        string prompt = await Connections.CreateLink(invocation, CancellationToken.None);
        string ticket = Ticket(prompt);
        await Connections.Complete(Connections.Begin(ticket), Principal(user), CancellationToken.None);
    }
    internal static string Ticket(string prompt) => new Uri(prompt.Split('\n').Single(s => s.StartsWith("https://"))).Query["?ticket=".Length..];
    internal static ClaimsPrincipal Principal(string user, string tenant = Tenant) => new(new ClaimsIdentity(
        [new("tid", tenant), new("oid", user), new("uid", user), new("utid", tenant)], "synthetic-middleware-validated"));
    internal static MessageActivity Activity(string text, string type = "channel", string user = Human, string channel = Channel)
    {
        object channelData = type == "channel"
            ? new { tenant = new { id = Tenant }, team = new { aadGroupId = Team }, channel = new { id = channel } }
            : new { tenant = new { id = Tenant } };
        return JsonSerializer.Deserialize<MessageActivity>(JsonSerializer.Serialize(new
        {
            type = "message", id = Guid.NewGuid().ToString("N"), text, channelId = "msteams",
            from = new { id = "synthetic-human", aadObjectId = user },
            recipient = new { id = "synthetic-au", aadObjectId = Au, agenticUserId = Au, agenticAppId = Agent,
                agenticAppBlueprintId = Blueprint, tenantId = Tenant },
            conversation = new { id = type == "channel" ? channel : type == "groupChat" ? "19:group" : "19:personal", conversationType = type },
            channelData
        }), JsonSerializerOptions.Web)!;
    }
    internal static string Confirmation(string output) => output.Split('\n').Single(s => s.StartsWith("/confirm "))[9..];
    public async ValueTask DisposeAsync() { await Connections.DisposeAsync(); await AuSession.DisposeAsync(); await Session.DisposeAsync(); }
}
internal sealed class GuardedAuTokens(LabSettings settings) : IWorkIqTokenProvider
{
    public Task<string> GetTokenAsync(CancellationToken ct)
    {
        string token = SyntheticToken.CreateAu();
        AgentUserTokenProvider.ValidateTokenShape(token, settings);
        return Task.FromResult(token);
    }
}

internal sealed class FakeHumanCache : IHumanTokenCache
{
    internal readonly Dictionary<string, string> Tokens = [];
    internal readonly List<string> Acquired = [], Removed = [];
    internal bool Deny;
    public Task<string> Acquire(ClaimsPrincipal user, CancellationToken ct)
    {
        string id = user.FindFirst("oid")!.Value;
        Acquired.Add(id);
        if (Deny) throw new HumanSignInRequiredException();
        return Task.FromResult(Tokens.TryGetValue(id, out string? token) ? token : SyntheticToken.Create(id));
    }
    public Task Remove(ClaimsPrincipal user)
    {
        string id = user.FindFirst("oid")!.Value;
        Tokens.Remove(id); Removed.Add(id);
        return Task.CompletedTask;
    }
}
internal static class SyntheticToken
{
    internal static string CreateAu(string? key = null, object? value = null)
    {
        Dictionary<string, object?> claims = new()
        {
            ["aud"] = HumanSettings.ResourceAppId, ["tid"] = Harness.Tenant, ["oid"] = Harness.Au,
            ["azp"] = Harness.Agent, ["xms_par_app_azp"] = Harness.Blueprint, ["xms_sub_fct"] = "13",
            ["scp"] = "WorkIQAgent.Ask", ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["iss"] = $"https://login.microsoftonline.com/{Harness.Tenant}/v2.0"
        };
        if (key is not null) { if (value is null) claims.Remove(key); else claims[key] = value; }
        return Encode(new { alg = "RS256", typ = "JWT" }) + "." + Encode(claims) + "." + Encode("synthetic-invalid-signature");
    }
    internal static string Create(string user, string? key = null, object? value = null)
    {
        Dictionary<string, object?> claims = new()
        {
            ["aud"] = HumanSettings.ResourceAppId, ["tid"] = Harness.Tenant, ["oid"] = user,
            ["azp"] = Harness.HumanClient, ["scp"] = "WorkIQAgent.Ask",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["iss"] = $"https://login.microsoftonline.com/{Harness.Tenant}/v2.0"
        };
        if (key is not null) { if (value is null) claims.Remove(key); else claims[key] = value; }
        return Encode(new { alg = "RS256", typ = "JWT" }) + "." + Encode(claims) + "." + Encode("synthetic-invalid-signature");
    }
    private static string Encode(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// All HTTP terminates in this handler. No sockets, Entra calls or tenant resources.
internal sealed class McpHandler : HttpMessageHandler
{
    internal const string SyntheticText = "WorkIQ Lab 13 synthetic file.\n";
    internal int Initializes, Deletes, AuthRequests, ToolCalls, Mutations;
    internal int CatalogCalls, CatalogExpirations;
    internal JsonArray? CatalogTools;
    internal string? CatalogCursor;
    internal JsonObject? DiscoveryResult;
    internal JsonObject? DiscoveryMcpResult;
    internal bool DiscoveryChunked;
    internal long DiscoveryEnvelopeBytes;
    internal bool DiscoveryProtocolError;
    internal int DiscoveryProtocolCode = -32602;
    internal string DiscoveryProtocolMessage = "PRIVATE protocol echo";
    internal int Expirations, DelayMs;
    internal HttpStatusCode? ToolStatus;
    internal HttpStatusCode? MutationHttpStatus;
    internal HttpStatusCode? InitializeStatus;
    internal bool FailDeleteAuth;
    internal bool CancelInitialize;
    internal Action? OnMutation;
    internal int? MutationResourceStatus;
    internal JsonObject? MutationResponse;
    internal bool MutationTimeout, MalformedMutation;
    internal Action? OnFetch;
    internal readonly List<(string Tool, JsonElement Args)> Calls = [];
    internal readonly List<JsonElement> WireToolRequests = [];
    internal readonly List<string> Bearers = [];
    internal readonly Dictionary<string, JsonObject> Items = new(StringComparer.Ordinal);
    internal byte[]? BlobBytes;
    internal JsonObject? BlobEnvelope;
    internal JsonObject? BlobMcpResult;
    internal string? BlobRawResponse;
    internal int? BlobRpcError;
    internal bool BlobChunked, BlobIoFailure, BlobEventStream;
    internal bool BlobAsText;
    internal HttpStatusCode? BlobHttpStatus;
    internal Action? OnBlob;
    internal string SetupOwner = Harness.Human;
    internal string ChannelFolderId = "channel-folder";
    internal JsonObject? ToolError;
    internal readonly Dictionary<string, JsonObject> SectionTwoResults = new(StringComparer.Ordinal);
    internal bool SectionTwoText;
    internal Func<string, JsonObject?>? SectionTwoResource;

    internal McpHandler()
    {
        Add("root", "Test", "channel-folder", true);
        Items["root"]["webUrl"] = "https://synthetic.sharepoint.com/sites/lab/Documents/Test";
        Add("channel-folder", "Channel", "drive-root", true);
        Add("drive-root", "Drive", "", true);
        Add("file", "sample.txt", "root", false);
        Add("dest", "Destination", "root", true);
        Add("outsider", "outside.txt", "drive-root", false);
        Add("human-root", "Human", "", true, "human-drive");
        Items["human-root"]["root"] = new JsonObject();
        Add("human-file", "personal.txt", "human-root", false, "human-drive");
        Add("other-root", "Other", "", true, "other-drive");
        Items["other-root"]["root"] = new JsonObject();
        Add("other-file", "other.txt", "other-root", false, "other-drive");
    }
    private void Add(string id, string name, string parent, bool folder, string drive = "drive")
    {
        JsonObject item = new()
        {
            ["id"] = id, ["name"] = name, ["eTag"] = "etag-" + id,
            ["parentReference"] = new JsonObject { ["driveId"] = drive, ["id"] = parent },
            ["size"] = Encoding.UTF8.GetByteCount(SyntheticText),
            ["@microsoft.graph.downloadUrl"] = "https://never-call.invalid/file?secret=SIGNED-CREDENTIAL"
        };
        item[folder ? "folder" : "file"] = folder ? new JsonObject() : new JsonObject { ["mimeType"] = "text/plain" };
        Items[id] = item;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri?.AbsoluteUri != WorkIqSession.Endpoint) throw new Exception("Unexpected egress.");
        AuthRequests++;
        Bearers.Add(request.Headers.Authorization?.Parameter ?? throw new Exception("No AU bearer."));
        if (request.Method == HttpMethod.Delete)
        {
            Deletes++;
            if (FailDeleteAuth) throw new AuWorkIqAuthenticationException();
            return new(HttpStatusCode.OK);
        }
        if (request.Method != HttpMethod.Post) throw new Exception("Unexpected HTTP method.");
        using JsonDocument doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("id", out JsonElement id)) return new(HttpStatusCode.Accepted);
        string method = root.GetProperty("method").GetString()!;
        if (method == "server/discover") return Json(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Not supported" } });
        if (method == "initialize")
        {
            Initializes++;
            if (CancelInitialize) throw new OperationCanceledException("synthetic private initialization message");
            if (InitializeStatus is { } initializationStatus) return new(initializationStatus);
            HttpResponseMessage response = Json(new { jsonrpc = "2.0", id, result = new
            {
                protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString(),
                capabilities = new { tools = new { } }, serverInfo = new { name = "synthetic-files", version = "1" }
            } });
            response.Headers.Add("Mcp-Session-Id", "synthetic-" + Initializes);
            return response;
        }
        if (method == "tools/list")
        {
            CatalogCalls++;
            if (CatalogExpirations > 0) { CatalogExpirations--; return new(HttpStatusCode.NotFound); }
            return Json(new { jsonrpc = "2.0", id, result = new
            {
                tools = CatalogTools ?? throw new Exception("No catalog fixture configured."),
                nextCursor = CatalogCursor
            } });
        }
        if (method != "tools/call") throw new Exception("Unexpected MCP method.");
        WireToolRequests.Add(root.Clone());
        ToolCalls++;
        JsonElement p = root.GetProperty("params");
        string tool = p.GetProperty("name").GetString()!;
        JsonElement args = p.GetProperty("arguments").Clone();
        Calls.Add((tool, args));
        if (!request.Headers.Contains("Mcp-Session-Id")) throw new Exception("Session header missing.");
        if (Expirations > 0) { Expirations--; return new(HttpStatusCode.NotFound); }
        if (ToolStatus is { } status) return new(status);
        if (ToolError is not null)
            return Json(new { jsonrpc = "2.0", id, result = new { isError = true,
                content = Array.Empty<object>(),
                structuredContent = ToolError } });
        if (DelayMs > 0) await Task.Delay(DelayMs, ct);
        object result;
        if ((tool is "search_paths" or "get_schema") && DiscoveryProtocolError)
            return Json(new { jsonrpc = "2.0", id, error = new { code = DiscoveryProtocolCode, message = DiscoveryProtocolMessage } });
        if ((tool is "search_paths" or "get_schema") && DiscoveryMcpResult is not null)
        {
            string body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = DiscoveryMcpResult });
            DiscoveryEnvelopeBytes = Encoding.UTF8.GetByteCount(body);
            return new(HttpStatusCode.OK) { Content = DiscoveryChunked ? new ChunkedJson(body) :
                new StringContent(body, Encoding.UTF8, "application/json") };
        }
        if (tool is "search_paths" or "get_schema")
            result = DiscoveryResult ?? new JsonObject { ["schema"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } }
            }};
        else if (tool == "list_agents")
            result = new { fixture = "Synthetic agent discovery metadata; not a captured service response." };
        else if (tool == "fetch")
        {
            OnFetch?.Invoke();
            result = new { results = args.GetProperty("entityUrls").EnumerateArray().Select(path => Fetch(path.GetString()!)).ToArray() };
            if (SectionTwoText)
                return Json(new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "text",
                    text = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result) } } }) } } } });
        }
        else if (tool == "fetch_blob")
        {
            if (BlobHttpStatus is { } blobStatus) return new(blobStatus);
            OnBlob?.Invoke();
            if (BlobIoFailure) throw new IOException("PRIVATE-IO-FAILURE");
            if (BlobRpcError is int rpcError)
                return Json(new { jsonrpc = "2.0", id, error = new { code = rpcError, message = "PRIVATE-PROTOCOL-FAILURE" } });
            if (BlobMcpResult is not null || BlobRawResponse is not null)
            {
                string body = BlobRawResponse ?? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = BlobMcpResult });
                if (BlobEventStream) body = "event: message\ndata: " + body + "\n\n";
                HttpContent content = BlobChunked ? new ChunkedJson(body) :
                    new StringContent(body, Encoding.UTF8, "application/json");
                if (BlobEventStream) content.Headers.ContentType = new("text/event-stream");
                return new(HttpStatusCode.OK) { Content = content };
            }
            byte[] bytes = BlobBytes ?? Encoding.UTF8.GetBytes(SyntheticText);
            result = BlobEnvelope ?? (object)new { statusCode = 200, sizeBytes = bytes.Length,
                base64Content = Convert.ToBase64String(bytes), contentType = "text/plain" };
            if (BlobAsText)
                return Json(new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result) } } } });
        }

        else
        {
            Mutations++;
            OnMutation?.Invoke();
            if (MutationHttpStatus is { } mutationStatus) return new(mutationStatus);
            if (MutationTimeout) throw new TaskCanceledException("Synthetic ambiguous timeout.");
            if (MalformedMutation) return Json(new { jsonrpc = "2.0", id, result = new { content = Array.Empty<object>(), structuredContent = new { unknown = true } } });
            result = (object?)MutationResponse ?? new { statusCode = MutationResourceStatus ?? (tool == "create_entity" ? 201 : 200), data = new { id = "synthetic-created" } };
        }
        return Json(new { jsonrpc = "2.0", id, result = new { content = Array.Empty<object>(), structuredContent = result } });
    }
    private object Fetch(string path)
    {
        if (SectionTwoResource?.Invoke(path) is JsonObject resource) return resource;
        if (SectionTwoResults.TryGetValue(path, out JsonObject? sectionData))
            return new { statusCode = 200, data = sectionData };
        if (path.Contains("/filesFolder?") && path.Contains("$select=") &&
            path.Split("$select=", 2)[1].Split('&')[0].Split(',').Contains("remoteItem"))
            throw new Exception("filesFolder does not support selecting remoteItem.");
        object data;
        if (path == $"/users/{Harness.Human}/drive?$select=id,owner")
            data = new { id = "human-drive", owner = new { user = new { id = SetupOwner } } };
        else if (path == $"/users/{Harness.Human}/drive") data = new { id = "human-drive" };
        else if (path.StartsWith("/drives/human-drive/root")) data = Items["human-root"];
        else if (path.StartsWith("/teams/")) data = Items[ChannelFolderId];
        else if (path.StartsWith("/shares/")) data = Items["file"];
        else if (path.StartsWith("/chats/"))
        {
            string[] users = [Harness.Human, Harness.Other, Harness.Au];
            data = new Dictionary<string, object?>
            {
                ["value"] = users.Select(user => new Dictionary<string, object?>
                {
                    ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
                    ["displayName"] = user == Harness.Au ? "Test AU" : "Test human",
                    ["id"] = "WRONG-CONVERSATION-MEMBER-ID-" + user,
                    ["userId"] = user, ["tenantId"] = Harness.Tenant
                }).ToArray(),
                ["@odata.nextLink"] = null
            };
        }
        else if (path.Contains("/children?") || path.Contains("/search("))
        {
            string parent = Uri.UnescapeDataString(path.Split('/')[4]);
            JsonObject[] children = Items.Values.Where(i => i["parentReference"]?["id"]?.GetValue<string>() == parent).ToArray();
            data = new Dictionary<string, object?>
            {
                ["value"] = children,
                ["@odata.nextLink"] = null
            };
        }
        else if (path.StartsWith("/drives/"))
        {
            string itemId = Uri.UnescapeDataString(path.Split('?')[0].Split('/')[4]);
            if (!Items.TryGetValue(itemId, out JsonObject? item)) return new { statusCode = 404, data = new { } };
            JsonObject copy = (JsonObject)item.DeepClone();
            data = copy;
        }
        else throw new Exception("Unexpected synthetic path: " + path);
        return new { statusCode = 200, data };
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}

internal sealed class ChunkedJson : HttpContent
{
    private readonly string json;
    internal ChunkedJson(string json) { this.json = json; Headers.ContentType = new("application/json"); }
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(json)).AsTask();
}
