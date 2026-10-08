using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static string[] IssuedHandles(string metadata)
    {
        if (metadata.Length == 0) return [];
        using JsonDocument doc = JsonDocument.Parse(metadata[metadata.IndexOf('{')..]);
        return doc.RootElement.GetProperty("handles").EnumerateArray().Select(h => h.GetProperty("handle").GetString()!).ToArray();
    }
    private static string[] ModelHandles(JsonElement request) => request.GetProperty("messages").EnumerateArray()
        .Where(m => m.GetProperty("role").GetString() == "system")
        .Select(m => m.GetProperty("content").GetString()!)
        .Where(s => s.StartsWith("\nApp-owned continuation handles", StringComparison.Ordinal))
        .SelectMany(IssuedHandles).ToArray();
    private static async Task DirectContinuationHandleChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (string scenario in new[] { "relative", "absolute", "long", "unicode", "400", "chain", "observe-only", "reuse", "unknown" })
            await check("Installed opaque page selection preserves native query and counts " + scenario, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                string initial = PagePath + "?$select=id&$top=50";
                string token = (scenario == "long" ? new string('X', 12000) : "X") + "%2f%2B+%E2%82%AC";
                if (scenario == "unicode") token += "原😀é";
                string page = PagePath + "?$select=body,from,id&$top=10&$skiptoken=" + token;
                string raw = scenario == "absolute" ? "https://graph.microsoft.com/v1.0" + page : page;
                string third = PagePath + "?$skiptoken=next%2b&$select=id,body&$top=7";
                h.Handler.SectionTwoResults[initial] = Page(raw);
                h.Handler.SectionTwoResults[page] = scenario == "400"
                    ? new() { ["statusCode"] = 400, ["error"] = new JsonObject { ["code"] = "BadRequest" } }
                    : Page(scenario == "chain" ? third : null);
                h.Handler.SectionTwoResults[third] = Page(null);
                if (scenario == "400") h.Handler.SectionTwoResource = url => url == page
                    ? new() { ["statusCode"] = 400, ["error"] = new JsonObject { ["code"] = "BadRequest" } } : null;
                ModelHandler model = new();
                string? selected = null;
                model.Respond = request =>
                {
                    int turn = model.Requests.Count;
                    if (turn == 1)
                    {
                        Must(request.GetProperty("tools").EnumerateArray().Any(t =>
                            t.GetProperty("function").GetProperty("name").GetString() == DirectContinuationHandles.ToolName));
                        return Completion(tool: "fetch", args: Fetch(initial), id: "first");
                    }
                    string[] handles = ModelHandles(request);
                    Must(handles.Length >= 1);
                    if (turn == 2 && scenario != "observe-only")
                    {
                        selected = scenario == "unknown" ? "page_invalid" : handles[0];
                        return Completion(tool: DirectContinuationHandles.ToolName, args: new { handle = selected }, id: "page");
                    }
                    if (turn == 3 && scenario is "chain" or "reuse")
                        return Completion(tool: DirectContinuationHandles.ToolName,
                            args: new { handle = scenario == "reuse" ? selected : handles.Last() }, id: "third");
                    return Completion("Observed pages only; partial on rejection.");
                };
                const string prompt = "Read channel roots with WorkIQ and report incomplete coverage honestly.";
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(prompt), default))!;
                int calls = scenario is "observe-only" or "unknown" ? 1 : scenario == "chain" ? 3 : 2;
                Must(h.Handler.ToolCalls == calls && h.Handler.Mutations == 0, output);
                if (calls > 1)
                {
                    Must(h.Handler.WireToolRequests[1].GetProperty("params").GetProperty("name").GetString() == "fetch");
                    Must(h.Handler.WireToolRequests[1].GetProperty("params").GetProperty("arguments").GetRawText() ==
                        JsonSerializer.SerializeToElement(Fetch(page)).GetRawText());
                    Must(output.Contains("App continuation adapter selection; sourceCall=1") &&
                        output.Contains("rawTokenMatch=true") &&
                        output.Contains(scenario == "absolute" ? "knownOriginEquivalent=true" : "exactUrlMatch=true"), output);
                }
                if (scenario == "chain") Must(h.Handler.WireToolRequests[2].GetProperty("params").GetProperty("arguments").GetRawText() ==
                    JsonSerializer.SerializeToElement(Fetch(third)).GetRawText());
                if (scenario is "reuse" or "unknown") Must(output.Contains("Continuation handle unavailable"), output);
                if (scenario == "400") Must(output.Contains("400"), output);
                Must(output.Contains("App continuation adapter selections: " +
                    (scenario == "observe-only" ? "0" : scenario is "chain" or "reuse" ? "2" : "1")), output);
                foreach (JsonElement request in model.Requests)
                {
                    Must(request.GetProperty("messages")[2].GetProperty("content").GetString() == prompt);
                    foreach (JsonElement message in request.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "system"))
                        if (message.GetProperty("content").GetString()!.StartsWith("\nApp-owned continuation handles"))
                            Must(!message.GetProperty("content").GetString()!.Contains(token));
                }
            });

        foreach (string bad in new[] { "https://evil.invalid/x", "https://graph.microsoft.com.evil.invalid/v1.0/x",
            "https://user@graph.microsoft.com/v1.0/x", "https://graph.microsoft.com:443/v1.0/x",
            "https://graph.microsoft.com/beta/x", "https://GRAPH.microsoft.com/v1.0/x", "//graph.microsoft.com/x", "/x?x=%Q0", "/x#fragment",
            "/x/../y?q=z", "/x/%2e%2e/y?q=z", "/x/%252e%252e/y?q=z", "/x\\y?q=z", "/x?x=a b" })
            await check("Continuation handle rejects unsafe service URL without dispatch " + bad, () =>
            {
                Must(DirectContinuationHandles.Relative(bad) is null, bad);
                return Task.CompletedTask;
            });

        foreach (string kind in new[] { "single", "attributed-reversed", "unattributed", "conflict", "conflicting-results", "foreign-resource",
            "error", "body-spoof", "schema", "mirror", "malformed", "limit" })
            await check("Handle registration reuses protocol walker and honest attribution " + kind, () =>
            {
                DirectContinuationHandles pages = new();
                DirectContinuationDiagnostic diagnostic = new();
                string link = PagePath + "?$skiptoken=A%2f+&$select=id";
                JsonObject page = Page(link);
                JsonElement args = PageArgs(PagePath);
                JsonObject envelope = new() { ["results"] = new JsonArray(new JsonObject { ["statusCode"] = 200, ["data"] = page }) };
                if (kind is "attributed-reversed" or "unattributed")
                {
                    args = PageArgs("/other", PagePath);
                    JsonObject first = new() { ["statusCode"] = 200, ["data"] = page.DeepClone() };
                    JsonObject second = new() { ["statusCode"] = 200, ["data"] = Page("/other?$skiptoken=B") };
                    if (kind == "attributed-reversed") { first["entityUrl"] = PagePath; second["entityUrl"] = "/other"; }
                    envelope["results"] = new JsonArray(first, second);
                }
                if (kind == "conflict") page["nextLink"] = PagePath + "?$skiptoken=CONFLICT";
                if (kind == "conflicting-results")
                {
                    envelope["results"] = new JsonArray(
                        new JsonObject { ["entityUrl"] = PagePath, ["statusCode"] = 200, ["data"] = page.DeepClone() },
                        new JsonObject { ["entityUrl"] = PagePath, ["statusCode"] = 200, ["data"] = Page(PagePath + "?$skiptoken=DIFFERENT") });
                }
                if (kind == "foreign-resource") page["@odata.nextLink"] = "/other?$skiptoken=A";
                if (kind == "error") envelope["error"] = new JsonObject { ["code"] = "BadRequest" };
                if (kind == "body-spoof") envelope = new() { ["body"] = page.DeepClone() };
                var result = PageResult(envelope);
                if (kind == "mirror") result.Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = envelope.ToJsonString() }];
                if (kind == "malformed") result.Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = "{bad" }];
                diagnostic.Observe(1, kind == "schema" ? "get_schema" : "fetch", args, result, pages.Capture, pages.Unavailable);
                if (kind == "limit") for (int i = 0; i < 66; i++) pages.Capture(link + i, i, "data", PagePath);
                string metadata = pages.Publish(1);
                string[] handles = IssuedHandles(metadata);
                int expected = kind == "attributed-reversed" ? 2 : kind is "single" or "mirror" ? 1 : 0;
                Must(handles.Length == expected && !metadata.Contains(link), metadata);
                if (handles.Length > 0)
                {
                    var resolved = pages.Resolve(JsonSerializer.SerializeToElement(new { handle = handles[0] }));
                    Must(resolved.Args.GetProperty("entityUrls")[0].GetString() == link);
                    bool rejected = false;
                    try { new DirectContinuationHandles().Resolve(JsonSerializer.SerializeToElement(new { handle = handles[0] })); }
                    catch (LabException) { rejected = true; }
                    Must(rejected);
                }
                return Task.CompletedTask;
            });
        foreach (string mode in new[] { "no-fetch", "collision", "legacy", "private" })
            await check("Continuation adapter catalog and private scope gating " + mode, async () =>
            {
                ModelHandler model = new(Completion("No data requested."));
                if (mode is "legacy" or "private")
                {
                    await using PrivateHarness p = new(model);
                    PrivateJob job = await p.Initiate();
                    if (mode == "legacy") job = p.Legacy(job);
                    await p.Authenticate(await p.Approve(job)); await p.Worker.RunOne(default);
                    bool offered = model.Requests.Single().GetProperty("tools").EnumerateArray().Any(t =>
                        t.GetProperty("function").GetProperty("name").GetString() == DirectContinuationHandles.ToolName);
                    Must(offered == (mode == "private") && p.H.Handler.ToolCalls == 0);
                    Must(!p.Routing.Requests.Single().GetRawText().Contains("\"name\":\"fetch_next_page\""));
                }
                else
                {
                    await using Harness h = new(); DirectEnable(h);
                    if (mode == "no-fetch")
                    {
                        var fetch = h.Handler.CatalogTools!.Single(n => n!["name"]!.GetValue<string>() == "fetch");
                        h.Handler.CatalogTools!.Remove(fetch);
                    }
                    else h.Handler.CatalogTools!.Add(new JsonObject { ["name"] = DirectContinuationHandles.ToolName,
                        ["inputSchema"] = new JsonObject { ["type"] = "object" } });
                    string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Explain only"), default))!;
                    Must(h.Handler.ToolCalls == 0);
                    if (mode == "collision") Must(model.Requests.Count == 0 && output.Contains("reserved app-owned"), output);
                    else Must(!model.Requests.Single().GetProperty("tools").EnumerateArray().Any(t =>
                        t.GetProperty("function").GetProperty("name").GetString() == DirectContinuationHandles.ToolName));
                }
            });
        await check("PrivateNative handle dispatch uses installed pinned human pipeline", async () =>
        {
            string first = PagePath + "?$top=50", next = PagePath + "?$top=10&$select=body,id&$skiptoken=A%2b+";
            ModelHandler model = new();
            model.Respond = request => model.Requests.Count switch
            {
                1 => Completion(tool: "fetch", args: Fetch(first), id: "one"),
                2 => Completion(tool: DirectContinuationHandles.ToolName, args: new { handle = ModelHandles(request).Single() }, id: "two"),
                _ => Completion("Partial observed data only.")
            };
            await using PrivateHarness p = new(model);
            p.H.Handler.SectionTwoResults[first] = Page(next);
            p.H.Handler.SectionTwoResults[next] = Page(null);
            PrivateJob job = await p.Initiate();
            await p.Authenticate(await p.Approve(job)); await p.Worker.RunOne(default);
            Must(p.H.Handler.ToolCalls == 2 && p.H.Handler.Mutations == 0 && model.Requests.Count == 3);
            Must(p.H.Handler.WireToolRequests[1].GetProperty("params").GetProperty("arguments").GetRawText() ==
                JsonSerializer.SerializeToElement(Fetch(next)).GetRawText());
            Must(p.Store.Get(job.Id).Result!.Contains("App continuation adapter selections: 1"));
        });
        await check("Multiple native tool results precede app handle metadata in installed transcript", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.SectionTwoResults[PagePath] = Page(PagePath + "?$skiptoken=A");
            h.Handler.SectionTwoResults["/other"] = Page("/other?$skiptoken=B");
            JsonObject first = Completion(tool: "fetch", args: Fetch(PagePath), id: "one");
            first["choices"]![0]!["message"]!["tool_calls"]!.AsArray().Add(
                Completion(tool: "fetch", args: Fetch("/other"), id: "two")["choices"]![0]!["message"]!["tool_calls"]![0]!.DeepClone());
            ModelHandler model = new(first, Completion("Partial pages; no continuation requested."));
            await App(h, model, DirectOptions()).Handle(Harness.Activity("Read two metadata pages"), default);
            JsonElement[] messages = model.Requests[1].GetProperty("messages").EnumerateArray().ToArray();
            int assistant = Array.FindIndex(messages, m => m.TryGetProperty("tool_calls", out _));
            Must(messages[assistant + 1].GetProperty("role").GetString() == "tool" &&
                messages[assistant + 2].GetProperty("role").GetString() == "tool" &&
                messages[assistant + 3].GetProperty("role").GetString() == "system");
            Must(ModelHandles(model.Requests[1]).Length == 2 && h.Handler.ToolCalls == 2);
        });
        await check("Handle argument shape identity extras and duplicate keys cannot resolve", () =>
        {
            foreach (string json in new[] { "null", "[]", """{"handle":1}""",
                """{"handle":"page_unknown","agentId":"other"}""", """{"handle":"x","handle":"y"}""" })
            {
                bool denied = false;
                try { new DirectContinuationHandles().Resolve(Json(json)); }
                catch (LabException) { denied = true; }
                Must(denied);
            }
            return Task.CompletedTask;
        });
        await check("Unpresentable native page cannot publish handles or select a continuation", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.SectionTwoResults[PagePath] = Page(PagePath + "?$skiptoken=" + new string('A', 2000));
            NaturalLanguageOptions options = DirectOptions();
            options.Budgets.ToolResultChars = 1024;
            options.Budgets.SchemaOutlineBytes = 1024;
            ModelHandler model = new(Completion(tool: "fetch", args: Fetch(PagePath), id: "one"));
            string output = (await App(h, model, options).Handle(Harness.Activity("Read channel pages"), default))!;
            Must(h.Handler.ToolCalls == 1 && model.Requests.Count == 1 && h.Handler.Mutations == 0 &&
                output.Contains("ToolResultChars") && output.Contains("App continuation adapter selections: 0"), output);
        });
        await check("Handle source limits concurrent registries and consumed entries stay isolated", async () =>
        {
            async Task<(DirectContinuationHandles Registry, string Handle)> Registry()
            {
                DirectContinuationHandles pages = new();
                await Task.Yield();
                pages.Capture(PagePath + "?$skiptoken=X", 1, "data", PagePath);
                return (pages, IssuedHandles(pages.Publish(1)).Single());
            }
            var pair = await Task.WhenAll(Registry(), Registry());
            Must(pair[0].Handle != pair[1].Handle);
            bool denied = false;
            try { pair[1].Registry.Resolve(JsonSerializer.SerializeToElement(new { handle = pair[0].Handle })); }
            catch (LabException) { denied = true; }
            Must(denied);
            pair[0].Registry.Resolve(JsonSerializer.SerializeToElement(new { handle = pair[0].Handle }));
            pair[0].Registry.Capture(PagePath + "?$skiptoken=X", 1, "data", PagePath);
            Must(IssuedHandles(pair[0].Registry.Publish(2)).Length == 0);
            pair[0].Registry.Capture("https://graph.microsoft.com/v1.0" + PagePath + "?$skiptoken=X", 1, "data", PagePath);
            Must(IssuedHandles(pair[0].Registry.Publish(3)).Length == 0);
            DirectContinuationHandles bounded = new();
            for (int i = 0; i < 65; i++)
            {
                bounded.Capture(PagePath + "?$skiptoken=" + i, 1, "data", PagePath);
                Must(IssuedHandles(bounded.Publish(i)).Length == (i < 64 ? 1 : 0));
            }
        });
        foreach (string stop in new[] { "budget", "cancel", "profile" })
            await check("Adapter revalidates context cancellation and native call budget before dispatch " + stop, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                string next = PagePath + "?$skiptoken=NEXT";
                h.Handler.SectionTwoResults[PagePath] = Page(next);
                NaturalLanguageOptions options = DirectOptions();
                if (stop == "budget") options.Budgets.McpCalls = 2; // Catalog plus initial fetch.
                using CancellationTokenSource cancellation = new();
                ModelHandler model = new();
                model.Respond = request =>
                {
                    if (model.Requests.Count == 1) return Completion(tool: "fetch", args: Fetch(PagePath), id: "one");
                    if (stop == "cancel") cancellation.Cancel();
                    if (stop == "profile") options.DirectMcp.Principal = "SignedInHuman";
                    return Completion(tool: DirectContinuationHandles.ToolName,
                        args: new { handle = ModelHandles(request).Single() }, id: "two");
                };
                string output = (await App(h, model, options).Handle(Harness.Activity("Read channel pages"), cancellation.Token))!;
                Must(h.Handler.ToolCalls == 1 && h.Handler.Mutations == 0 &&
                    (output.Contains("limit") || output.Contains("cancelled") || output.Contains("changed")), output);
            });
    }
}
