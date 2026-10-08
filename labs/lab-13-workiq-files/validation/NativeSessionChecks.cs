using WorkIqFiles;

internal static class NativeSessionChecks
{
    private static void Must(bool ok) { if (!ok) throw new Exception("Native session assertion."); }
    private static Dictionary<string, object?> Args => new() { ["entityUrls"] = new[] { "/drives/drive/items/file" } };
    private static async Task Denied<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    internal static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("Native chunked schema envelope respects installed response budget", async () =>
        {
            await using Harness h = new();
            h.Handler.DiscoveryChunked = true;
            h.Handler.DiscoveryMcpResult = new() { ["content"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = new string('x', 300000) }) };
            using OperationProgress progress = new() { NaturalLanguageBudgets = NaturalLanguageBudgets.Standard };
            bool denied = false;
            try { await h.Session.CallAsync("get_schema",
                new() { ["path"] = "/teams/" + Harness.Team, ["operationType"] = "fetch", ["format"] = "jsonschema" }, false, default); }
            catch (Exception error) when (error is LabException or HttpRequestException or ModelContextProtocol.McpException)
            { denied = true; }
            Must(denied && h.Handler.ToolCalls == 1);
        });
        foreach (string failure in new[] { "cancel", "http" })
            await check("Native initialization failure sends no tool: " + failure, async () =>
            {
                await using Harness h = new();
                h.Handler.CancelInitialize = failure == "cancel";
                h.Handler.InitializeStatus = failure == "http" ? System.Net.HttpStatusCode.Forbidden : null;
                bool denied = false;
                try { await h.Session.CallAsync("fetch", Args, false, default); }
                catch (Exception error) when (error is OperationCanceledException or HttpRequestException or ModelContextProtocol.McpException)
                { denied = true; }
                Must(denied && h.Handler.ToolCalls == 0 && h.Handler.Initializes == 1);
            });
        await check("Native malformed write response never replays", async () =>
        {
            await using Harness h = new();
            h.Handler.MalformedMutation = true;
            await h.Session.CallAsync("create_entity", new()
                { ["parentUrl"] = "/drives/drive/items/root/children", ["jsonBody"] = """{"name":"test","folder":{}}""" }, true, default);
            Must(h.Handler.Mutations == 1);
        });
        await check("Native catalog refuses unfinished pagination", async () =>
        {
            await using Harness h = new();
            h.Handler.CatalogTools = new();
            h.Handler.CatalogCursor = "synthetic-cursor";
            await Denied<LabException>(() => h.Session.ListAsync(default));
            Must(h.Handler.CatalogCalls == 1);
        });
        await check("Native delete-session authentication failure does not dispatch data", async () =>
        {
            Harness h = new();
            await h.AuSession.CallAsync("fetch", Args, false, default);
            h.Handler.FailDeleteAuth = true;
            try { await h.DisposeAsync(); }
            catch (AuWorkIqAuthenticationException) { }
            Must(h.Handler.ToolCalls == 1);
        });
        await check("Native session reuse, fresh bearer, serialized queue and timings", async () =>
        {
            await using Harness h = new();
            h.Handler.DelayMs = 10;
            ToolReply first = await h.Session.CallAsync("fetch", Args, false, default);
            h.Tokens.Token = SyntheticToken.Create(Harness.Human, "jti", "renewed");
            ToolReply[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => h.Session.CallAsync("fetch", Args, false, default)));
            Must(h.Handler.Initializes == 1 && results.All(r => r.Timing.Mode == "reused") && results.Skip(1).All(r => r.Timing.QueueMs > 0));
            Must(results.All(r => r.Timing.InitializationMs == 0 && r.Timing.Session == first.Timing.Session));
            Must(h.Handler.Bearers.Last() == h.Tokens.Token && h.Tokens.Calls == h.Handler.AuthRequests);
        });
        foreach (int expirations in new[] { 1, 2 })
            await check("Native read reconnect bounded at one: " + expirations, async () =>
            {
                await using Harness h = new();
                h.Handler.Expirations = expirations;
                if (expirations == 1) await h.Session.CallAsync("fetch", Args, false, default);
                else await Denied<HttpRequestException>(() => h.Session.CallAsync("fetch", Args, false, default));
                Must(h.Handler.Initializes == 2);
            });
        await check("Native mutation cannot be misclassified as retryable read", async () =>
        {
            await using Harness h = new();
            await Denied<LabException>(() => h.Session.CallAsync("do_action", [], false, default));
            Must(h.Handler.ToolCalls == 0);
        });
        await check("Native egress fixed endpoint before bearer and redirects denied", async () =>
        {
            Tokens tokens = new("synthetic-token");
            using HttpClient http = new(new WorkIqSession.HumanHttpHandler(tokens) { InnerHandler = new RedirectHandler() });
            foreach (string url in new[] { "https://graph.microsoft.com/v1.0/me", WorkIqSession.Endpoint + "?q=token", "https://other.invalid/mcp" })
                await Denied<LabException>(() => http.GetAsync(url));
            Must(tokens.Calls == 0);
            await Denied<HttpRequestException>(() => http.GetAsync(WorkIqSession.Endpoint));
            Must(tokens.Calls == 1);
        });
        await check("Native cancellation while queued sends no second request", async () =>
        {
            await using Harness h = new();
            h.Handler.DelayMs = 50;
            Task<ToolReply> first = h.Session.CallAsync("fetch", Args, false, default);
            using CancellationTokenSource cancel = new(); cancel.Cancel();
            await Denied<OperationCanceledException>(() => h.Session.CallAsync("fetch", Args, false, cancel.Token));
            await first;
            Must(h.Handler.ToolCalls == 1);
        });
        await check("Native session disposal is idempotent and denies reuse", async () =>
        {
            Harness h = new();
            await h.Session.CallAsync("fetch", Args, false, default);
            await h.DisposeAsync(); await h.DisposeAsync();
            Must(h.Handler.Deletes == 1);
            await Denied<ObjectDisposedException>(() => h.Session.CallAsync("fetch", Args, false, default));
        });
    }
}
