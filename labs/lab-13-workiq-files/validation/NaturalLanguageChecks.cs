using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Teams.Apps.Schema;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static readonly string Team = "/teams/" + Harness.Team;
    private static readonly string Channel = Team + "/channels/" + FileCommand.E(Harness.Channel);
    private static readonly string Root = Channel + "/messages/100";
    private static void Must(bool ok, string detail = "") { if (!ok) throw new Exception("NL assertion: " + detail); }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static async Task Denied(Func<Task> run)
    {
        try { await run(); } catch (LabException) { return; }
        throw new Exception("Expected denied NL operation.");
    }
    private static JsonArray Tools()
    {
        JsonObject Schema(string property, JsonObject type, params string[] required) => new()
        {
            ["type"] = "object", ["properties"] = new JsonObject { [property] = type },
            ["required"] = new JsonArray(required.Select(x => JsonValue.Create(x)).ToArray()), ["additionalProperties"] = false
        };
        JsonObject Tool(string name, JsonObject schema) => new() { ["name"] = name, ["inputSchema"] = schema };
        return new(
            Tool("fetch", Schema("entityUrls", new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }, "entityUrls")),
            Tool("fetch_blob", Schema("path", new() { ["type"] = "string" }, "path")),
            Tool("search_paths", Schema("query", new() { ["type"] = "string" }, "query")),
            Tool("get_schema", new()
            {
                ["type"] = "object", ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string" },
                    ["operationType"] = new JsonObject { ["type"] = "string" },
                    ["format"] = new JsonObject { ["type"] = "string" }
                }, ["required"] = new JsonArray("path", "operationType", "format"), ["additionalProperties"] = false
            }),
            Tool("do_action", Schema("actionUrl", new() { ["type"] = "string" }, "actionUrl")));
    }
    private static void Enable(Harness h)
    {
        SectionTwoChecks.Enable(h);
        h.Handler.CatalogTools = Tools();
    }
    private static NaturalLanguageOptions Options(bool enabled = true) => new()
    { Enabled = enabled, ApiKey = "synthetic-model-key", Budgets = new() { Profile = "Standard" } };
    private static WorkIqIngress App(Harness h, ModelHandler model, NaturalLanguageOptions? options = null, TimeProvider? clock = null)
    {
        NaturalLanguageAgent nl = new(options ?? Options(), h.Router, h.Policy, h.Settings, new(), new(),
            o => new NaturalLanguageModel(o, model), clock);
        return new(h.Router, h.Settings, h.Policy, nl);
    }
    private static object Fetch(string path) => new { entityUrls = new[] { path } };
    private static JsonObject Completion(string? text = null, string? tool = null, object? args = null,
        string id = "call_1", string? finish = null) => new()
    {
        ["id"] = "chatcmpl_synthetic", ["object"] = "chat.completion", ["created"] = 1710000000, ["model"] = "fixture-deployment",
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0, ["finish_reason"] = finish ?? (tool is null ? "stop" : "tool_calls"),
            ["message"] = new JsonObject
            {
                ["role"] = "assistant", ["content"] = text,
                ["tool_calls"] = tool is null ? null : new JsonArray(new JsonObject
                {
                    ["id"] = id, ["type"] = "function", ["function"] = new JsonObject
                    {
                        ["name"] = tool, ["arguments"] = JsonSerializer.Serialize(args)
                    }
                })
            }
        })
    };
    internal static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await FullWorkIqGuideChecks(check);
        await DirectChecks(check);
        await MapSchemaChecks(check);
        await BudgetAndSchemaChecks(check);
        await TimingChecks(check);
        await check("NL SDK typed protocol code and message envelope", async () =>
        {
            await using Harness h = new(); Enable(h);
            h.Handler.DiscoveryProtocolError = true; h.Handler.DiscoveryProtocolCode = -32601;
            h.Handler.DiscoveryProtocolMessage = "Method not found";
            try
            {
                await h.AuSession.CallAsync("get_schema", new()
                { ["path"] = Team, ["operationType"] = "fetch", ["format"] = "jsonschema" }, false, default);
                throw new Exception("Expected synthetic protocol rejection.");
            }
            catch (WorkIqProtocolException error)
            {
                Must((int)error.ErrorCode == -32601 && error.OuterHttpStatus == 200);
                Must(error.Message == "Request failed (remote): Method not found", error.Message);
            }
        });
        await check("NL model egress never forwards key to another host/path", async () =>
        {
            ModelHandler inner = new(Completion("bad"));
            using HttpClient client = new(new ModelEgress(new Uri(Options().Endpoint + "/")) { InnerHandler = inner });
            using HttpRequestMessage request = new(HttpMethod.Post, "https://evil.invalid/chat/completions");
            request.Headers.Authorization = new("Bearer", "synthetic-model-key");
            await Denied(async () => { await client.SendAsync(request); });
            Must(inner.Requests.Count == 0);
        });
        await check("NL model endpoint canonical HTTPS only", async () =>
        {
            foreach (string endpoint in new[] { "http://test.openai.azure.com/openai/v1", "https://evil.invalid/openai/v1",
                Options().Endpoint + "?key=bad", "https://test.openai.azure.com:444/openai/v1", "https://user@test.openai.azure.com/openai/v1" })
                await Denied(() => { new NaturalLanguageOptions { Endpoint = endpoint, ApiKey = "test" }.Validate(); return Task.CompletedTask; });
        });
        await check("NL missing and malformed schema input handled explicitly", async () =>
        {
            await Denied(() => { ToolSchema.Check(default); return Task.CompletedTask; });
            await Denied(() => { ToolSchema.Check(Json(new { type = "string", maxLength = "bad" })); return Task.CompletedTask; });
        });
        await check("NL real session per-invocation call budget counts catalog separately", async () =>
        {
            await using Harness h = new(); Enable(h);
            using OperationProgress progress = new() { NaturalLanguageCallBudget = 1, DiscoveryCalls = new(), SectionOneCalls = new() };
            await h.AuSession.ListAsync(default);
            await Denied(async () => { await h.AuSession.CallAsync("fetch", new() { ["entityUrls"] = new[] { Team } }, false, default); });
            Must(h.Handler.CatalogCalls == 1 && h.Handler.ToolCalls == 0);
        });
        await check("NL bounded JSON schema supports nullable alternatives and denies duplicate keys", async () =>
        {
            using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"x":{"anyOf":[{"type":"null"},{"type":"string","maxLength":3}]}},"required":["x"],"additionalProperties":false}""");
            ToolSchema.Validate(schema.RootElement, Json(new { x = "abc" }));
            ToolSchema.Validate(schema.RootElement, Json(new { x = (string?)null }));
            await Denied(() => { ToolSchema.Validate(schema.RootElement, Json(new { x = "abcdef" })); return Task.CompletedTask; });
            using JsonDocument duplicate = JsonDocument.Parse("""{"x":null,"x":"ab"}""");
            await Denied(() => { ToolSchema.Validate(schema.RootElement, duplicate.RootElement); return Task.CompletedTask; });
        });
        await check("NL real serialized catalog session warm reuse and bounded reconnect", async () =>
        {
            await using Harness h = new(); Enable(h);
            ToolCatalog first = await h.AuSession.ListAsync(default);
            ToolCatalog second = await h.AuSession.ListAsync(default);
            Must(first.Timing.Mode == "newly initialized" && second.Timing.Mode == "reused" && h.Handler.Initializes == 1);
            h.Handler.CatalogExpirations = 1;
            ToolCatalog third = await h.AuSession.ListAsync(default);
            Must(third.Timing.Mode == "reconnected" && h.Handler.CatalogCalls == 4 && h.Handler.Initializes == 2);
        });
        await check("NL Azure error unknown field/header values are never echoed", async () =>
        {
            using HttpResponseMessage response = new(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"code":"api-key=PRIVATE","type":"Bearer PRIVATE","param":"messages[0].content.PRIVATE","message":"PRIVATE"}}""")
            };
            response.Headers.TryAddWithoutValidation("x-request-id", "PRIVATE-TOKEN");
            response.Headers.TryAddWithoutValidation("x-ms-request-id", ["aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "PRIVATE"]);
            string output = await AzureModelDiagnostic.Read(response, default);
            Must(!output.Contains("PRIVATE") && output.Contains("requestId [unavailable]") && output.Length < 1200);
        });
        await check("NL Azure error parsing obeys cancellation", async () =>
        {
            using HttpResponseMessage response = new(HttpStatusCode.BadRequest) { Content = new StringContent("{}") };
            using CancellationTokenSource cancel = new(); cancel.Cancel();
            try { await AzureModelDiagnostic.Read(response, cancel.Token); }
            catch (OperationCanceledException) { return; }
            throw new Exception("Expected diagnostic cancellation.");
        });
        await check("NL Azure streamed error without content length remains bounded", async () =>
        {
            using HttpResponseMessage response = new(HttpStatusCode.BadRequest)
            { Content = new ChunkedJson(new string('x', AzureModelDiagnostic.MaxBytes + 100) + "PRIVATE") };
            string output = await AzureModelDiagnostic.Read(response, default);
            Must(output.Contains("oversized-withheld") && !output.Contains("PRIVATE") && output.Length < 1200);
        });
    }
    private sealed class ModelHandler(params JsonObject[] replies) : HttpMessageHandler
    {
        internal readonly List<JsonElement> Requests = [];
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal bool Disposed;
        internal Action? BeforeResponse;
        internal Func<JsonElement, JsonObject>? Respond;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Must(request.RequestUri!.AbsoluteUri == Options().Endpoint + "/chat/completions");
            Must(request.Headers.Authorization?.Parameter == "synthetic-model-key");
            using JsonDocument doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add(doc.RootElement.Clone());
            BeforeResponse?.Invoke();
            ct.ThrowIfCancellationRequested();
            Must(Respond is not null || Requests.Count <= replies.Length, "Unexpected extra model call.");
            JsonObject reply = Respond?.Invoke(doc.RootElement) ?? replies[Requests.Count - 1];
            return new(Status) { Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
