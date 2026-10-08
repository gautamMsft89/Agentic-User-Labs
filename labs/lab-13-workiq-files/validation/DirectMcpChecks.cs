using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static NaturalLanguageOptions DirectOptions(string principal = "AgentUser") => new()
    {
        Enabled = true, FullWorkIqGuide = true, ApiKey = "synthetic-model-key", DirectMcp = new() { Enabled = true, Principal = principal },
        Budgets = new() { Profile = "Generous" }
    };
    private static void DirectEnable(Harness h)
    {
        Enable(h);
        JsonArray tools = ToolsWithOverrides();
        tools.RemoveAt(4);
        JsonObject Tool(string name, params string[] keys) => new()
        {
            ["name"] = name, ["description"] = "Synthetic native operation.",
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray(keys.Select(k => JsonValue.Create(k)).ToArray()),
                ["properties"] = new JsonObject(keys.Select(k => KeyValuePair.Create<string, JsonNode?>(k, new JsonObject { ["type"] = "string" }))),
                ["additionalProperties"] = false
            }
        };
        tools.Add(Tool("create_entity", "parentUrl", "jsonBody"));
        tools.Add(Tool("update_entity", "entityUrl", "jsonBody"));
        tools.Add(Tool("delete_entity", "entityUrl", "jsonBody"));
        tools.Add(Tool("do_action", "actionUrl", "jsonBody"));
        tools.Add(Tool("custom_operation", "jsonBody"));
        tools.Add(Tool("ask", "query"));
        tools.Add(Tool("list_agents"));
        h.Handler.CatalogTools = tools;
    }
    private static object CreateArgs(string name = "DirectFolder") =>
        new { parentUrl = "/drives/drive/items/root/children", jsonBody = JsonSerializer.Serialize(new { name, folder = new { } }) };
    private static JsonObject TwoWrites()
    {
        JsonObject first = Completion(tool: "create_entity", args: CreateArgs("One"), id: "one");
        first["choices"]![0]!["message"]!["tool_calls"]!.AsArray().Add(
            Completion(tool: "create_entity", args: CreateArgs("Two"), id: "two")["choices"]![0]!["message"]!["tool_calls"]![0]!.DeepClone());
        return first;
    }
    private static async Task DirectChecks(Func<string, Func<Task>, Task> check)
    {
        await DirectDuplicateChecks(check);
        await DirectTagChecks(check);
        await DirectMentionGuidanceChecks(check);
        await ContentFidelityChecks(check);
        await DirectBudgetChecks(check);
        await DirectResourceIdChecks(check);
        await DirectChatChecks(check);
        await DirectAttachmentChecks(check);
        await DirectImageChecks(check);
        await DirectImageProtocolChecks(check);
        await DirectProgressChecks(check);
        await PrivateConversationChecks(check);
        await check("Direct observed five-turn discovery sequence permits repeated agent discovery", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            // The live trace establishes filter arguments, not its complete catalog or result bodies.
            h.Handler.CatalogTools![2]!["inputSchema"] = JsonNode.Parse(
                """{"type":"object","properties":{"filter":{"type":"string"}},"required":["filter"],"additionalProperties":false}""");
            string[] filters = ["channels files drive items search file name content", "teams channels files", "drive"];
            ModelHandler model = new(
                Completion(tool: "search_paths", args: new { filter = filters[0] }, id: "search-one"),
                Completion(tool: "search_paths", args: new { filter = filters[1] }, id: "search-two"),
                Completion(tool: "list_agents", args: new { }, id: "agents-one"),
                Completion(tool: "search_paths", args: new { filter = filters[2] }, id: "search-three"),
                Completion(tool: "list_agents", args: new { }, id: "agents-two"),
                Completion("Discovery only; file contents not fetched."));
            string result = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Find a file"), default))!;
            Must(result.Contains("Discovery only") && h.Handler.ToolCalls == 5 && h.Handler.Mutations == 0 &&
                model.Requests.Count == 6 && result.Contains("5. turn 5 list_agents ") &&
                result.Contains("original jsonBody string retained"), result);
            Must(h.Handler.Calls.Select(c => c.Tool).SequenceEqual(
                ["search_paths", "search_paths", "list_agents", "search_paths", "list_agents"]));
            Must(h.Handler.Calls.Where(c => c.Tool == "search_paths")
                .Select(c => c.Args.GetProperty("filter").GetString()).SequenceEqual(filters));
            foreach (string token in h.Handler.Bearers) AgentUserTokenProvider.ValidateTokenShape(token, h.Settings);
        });
        foreach (string shape in new[] { "list_agents", "search-query", "search-filter", "fetch" })
            await check("Direct fresh model-selected identical read is not a write replay " + shape, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                string tool = shape.StartsWith("search-") ? "search_paths" : shape;
                object args = shape switch
                {
                    "search-query" => new { query = "drive" },
                    "search-filter" => new { filter = "drive" },
                    "fetch" => Fetch(Team),
                    _ => new { }
                };
                ModelHandler model = new(Completion(tool: tool, args: args, id: "first"),
                    Completion(tool: tool, args: args, id: "second"), Completion("Repeated read returned."));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Read again"), default))!;
                Must(output.Contains("Repeated read returned") && h.Handler.ToolCalls == 2 && h.Handler.Mutations == 0, output);
            });
        await check("Direct discovery classification stays narrow and respects effect annotations", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            var catalog = (await h.AuSession.ListAsync(default)).Tools;
            WorkIqTool agents = catalog.Single(t => t.Name == "list_agents") with { ReadOnlyHint = null };
            WorkIqTool search = catalog.Single(t => t.Name == "search_paths");
            Must(!DirectMcpContract.MayMutate(agents, Json(new { })) &&
                !DirectMcpContract.MayMutate(search, Json(new { filter = "drive" })));
            Must(DirectMcpContract.MayMutate(agents with { ReadOnlyHint = false }, Json(new { })) &&
                DirectMcpContract.MayMutate(agents with { DestructiveHint = true }, Json(new { })) &&
                DirectMcpContract.MayMutate(agents, Json(new { unknown = true })) &&
                DirectMcpContract.MayMutate(search, Json(new { filter = "drive", unknown = true })) &&
                DirectMcpContract.MayMutate(agents with { Name = "unknown_operation", ReadOnlyHint = true }, Json(new { })));
            await Denied(async () => await h.AuSession.CallDirectAsync(agents, new() { ["agentId"] = "other-agent" }, default));
            await Denied(async () => await h.AuSession.CallDirectAsync(agents, new() { ["headers"] = new { } }, default));
            Must(h.Handler.ToolCalls == 0);
        });
        await check("Direct agent discovery uses discovery recorder without write dispatch flag", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            using OperationProgress progress = new() { SectionOneCalls = new(), DiscoveryCalls = new() };
            WorkIqTool tool = (await h.AuSession.ListAsync(default)).Tools.Single(t => t.Name == "list_agents");
            await h.AuSession.CallDirectAsync(tool, [], default);
            Must(progress.DiscoveryCalls.ForTool("list_agents").Count == 1 &&
                progress.SectionOneCalls.ForTool("list_agents").Count == 0 && !progress.MutationMayHaveDispatched);
        });
        await check("Direct distinct later-turn writes are not blocked by prior write flag", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            ModelHandler model = new(
                Completion(tool: "create_entity", args: CreateArgs("First"), id: "first"),
                Completion(tool: "create_entity", args: CreateArgs("Second"), id: "second"),
                Completion("Both writes returned."));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Create two"), default))!;
            Must(output.Contains("Both writes returned") && h.Handler.Mutations == 2 && model.Requests.Count == 3, output);
        });
        await check("Direct unknown outcome after successful write stops later proposals without replay", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.OnMutation = () => { if (h.Handler.Mutations == 2) h.Handler.MutationTimeout = true; };
            ModelHandler model = new(
                Completion(tool: "create_entity", args: CreateArgs("Completed"), id: "completed"), TwoWrites());
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Create three"), default))!;
            Must(output.Contains("UNKNOWN OUTCOME") && h.Handler.Mutations == 2 &&
                h.Handler.ToolCalls == 2 && model.Requests.Count == 2, output);
        });
        foreach (string tool in new[] { "create_entity", "update_entity", "delete_entity", "do_action", "custom_operation" })
            await check("Direct native write executes immediately " + tool, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                object args = tool switch
                {
                    "create_entity" => CreateArgs(),
                    "do_action" => new { actionUrl = "/native/action", jsonBody = "{}" },
                    "custom_operation" => new { jsonBody = "{}" },
                    _ => new { entityUrl = "/drives/drive/items/file", jsonBody = "{}" }
                };
                ModelHandler model = new(Completion(tool: tool, args: args, id: "native-write"), Completion("Operation returned."));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Perform the operation"), default))!;
                Must(output.Contains("Operation returned.") && !output.Contains("/mcp-confirm") && !output.Contains("approval consumed") &&
                    h.Handler.Mutations == 1 && h.Handler.CatalogCalls == 1 && model.Requests.Count == 2, output);
                Must(JsonElement.DeepEquals(h.Handler.Calls.Single().Args, Json(args)));
                Must(model.Requests[1].GetProperty("messages").EnumerateArray().Last().GetProperty("tool_call_id").GetString() == "native-write");
            });
        await check("Direct multiple writes execute sequentially in same invocation with matching results", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            ModelHandler model = new(TwoWrites(), Completion("Two operations returned."));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Create two folders"), default))!;
            Must(h.Handler.Mutations == 2 && h.Handler.CatalogCalls == 1 && model.Requests.Count == 2 &&
                output.Contains("1. turn 1 create_entity") && output.Contains("2. turn 1 create_entity"), output);
            string[] ids = model.Requests[1].GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool")
                .Select(m => m.GetProperty("tool_call_id").GetString()!).ToArray();
            Must(ids.SequenceEqual(["one", "two"]));
        });
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.BadGateway })
            await check("Direct ambiguous write stops queued writes without replay " + status, async () =>
            {
                await using Harness h = new(); DirectEnable(h); h.Handler.MutationHttpStatus = status;
                ModelHandler model = new(TwoWrites());
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Create two folders"), default))!;
                Must(output.Contains("UNKNOWN OUTCOME") && !output.Contains("Confirmation consumed") &&
                    h.Handler.ToolCalls == 1 && h.Handler.Mutations == 1 && model.Requests.Count == 1, output);
            });
        foreach (string failure in new[] { "timeout", "resource-error", "host-cancel" })
            await check("Direct failed write stops remaining invocation " + failure, async () =>
            {
                await using Harness h = new(); DirectEnable(h); using CancellationTokenSource cancel = new();
                if (failure == "timeout") h.Handler.MutationTimeout = true;
                if (failure == "resource-error") h.Handler.MutationResourceStatus = 400;
                if (failure == "host-cancel") h.Handler.OnMutation = cancel.Cancel;
                ModelHandler model = new(TwoWrites());
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Create two"), cancel.Token))!;
                Must(h.Handler.Mutations == 1 && model.Requests.Count == 1 &&
                    (output.Contains("UNKNOWN OUTCOME") || output.Contains("effects may be partial") || output.Contains("cancelled")), output);
            });
        foreach (string tool in new[] { "create_entity", "update_entity", "delete_entity", "do_action", "custom_operation" })
            await check("Direct fresh identical model-selected write executes again " + tool, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                object args = tool switch
                {
                    "create_entity" => CreateArgs(),
                    "do_action" => new { actionUrl = "/native/action", jsonBody = "{}" },
                    "custom_operation" => new { jsonBody = "{}" },
                    _ => new { entityUrl = "/drives/drive/items/file", jsonBody = "{}" }
                };
                ModelHandler model = new(Completion(tool: tool, args: args, id: "first"),
                    Completion(tool: tool, args: args, id: "fresh-selection"), Completion("Two selected operations returned."));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Perform twice"), default))!;
                Must(output.Contains("Two selected operations returned") && h.Handler.Mutations == 2 &&
                    h.Handler.ToolCalls == 2 && model.Requests.Count == 3, output);
                Must(JsonElement.DeepEquals(h.Handler.Calls[0].Args, h.Handler.Calls[1].Args));
            });
        foreach (bool enabled in new[] { true, false })
            foreach (string verb in new[] { "confirm", "cancel" })
                await check("Direct obsolete command is inert " + enabled + "/" + verb, async () =>
                {
                    await using Harness h = new(); DirectEnable(h);
                    NaturalLanguageOptions options = DirectOptions(); options.DirectMcp.Enabled = enabled;
                    ModelHandler model = new();
                    WorkIqIngress app = App(h, model, options);
                    string command = "/mcp-" + verb + " " + new string('a', 48);
                    string output = (await app.Handle(Harness.Activity(command), default))!;
                    await app.Handle(Harness.Activity(command), default);
                    Must(output == WorkIqIngress.Retired && h.Handler.CatalogCalls == 0 && h.Handler.ToolCalls == 0 && model.Requests.Count == 0, output);
                });
        foreach (string change in new[] { "profile", "policy", "caller" })
            await check("Direct mid-request context change blocks write " + change, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                NaturalLanguageOptions options = DirectOptions();
                var activity = Harness.Activity("Create folder");
                ModelHandler model = new(Completion(tool: "create_entity", args: CreateArgs()));
                model.BeforeResponse = () =>
                {
                    if (change == "profile") options.DirectMcp.Principal = "SignedInHuman";
                    if (change == "policy") h.Policy.MaxItems++;
                    if (change == "caller") h.Policy.Contexts[0].RequesterIds = [Harness.Other];
                };
                string output = (await App(h, model, options).Handle(activity, default))!;
                Must(h.Handler.Mutations == 0 && output.Contains("stopped"), output);
            });
        await check("Direct requests retain no transcript or pending work across users", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            ModelHandler model = new(Completion(tool: "create_entity", args: CreateArgs()), Completion("Created."), Completion("Independent."));
            WorkIqIngress app = App(h, model, DirectOptions());
            await app.Handle(Harness.Activity("PRIVATE first request"), default);
            await app.Handle(Harness.Activity("Independent request", user: Harness.Other), default);
            Must(!model.Requests[2].GetRawText().Contains("PRIVATE") && h.Handler.Mutations == 1 && h.Handler.CatalogCalls == 2);
        });
        foreach (string profile in new[] { "AgentUser", "SignedInHuman" })
            await check("Direct fixed principal native catalog and immediate write " + profile, async () =>
            {
                await using Harness h = new(); DirectEnable(h); await h.Connect(Harness.Human);
                int acquired = h.Cache.Acquired.Count;
                ModelHandler model = new(Completion(tool: "create_entity", args: CreateArgs()), Completion("Returned."));
                string output = (await App(h, model, DirectOptions(profile)).Handle(Harness.Activity("Create", "personal"), default))!;
                Must(output.Contains("Returned.") && h.Handler.CatalogCalls == 1 && h.Handler.Mutations == 1, output);
                Must(profile == "AgentUser" ? h.Cache.Acquired.Count == acquired : h.Cache.Acquired.Count > acquired);
                if (profile == "AgentUser") foreach (string token in h.Handler.Bearers) AgentUserTokenProvider.ValidateTokenShape(token, h.Settings);
            });
        foreach (string context in new[] { "channel", "personal" })
            await check("Direct human unavailable has no AU fallback " + context, async () =>
            {
                await using Harness h = new(); DirectEnable(h); ModelHandler model = new();
                string output = (await App(h, model, DirectOptions("SignedInHuman")).Handle(Harness.Activity("Read", context), default))!;
                Must(h.Handler.CatalogCalls == 0 && model.Requests.Count == 0 && (output.Contains("/signin") || output.Contains("personal chat")), output);
            });
        await check("Direct native fetch outside channel scope remains one exact read", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            const string path = "/me/drive/root/children?$top=40";
            h.Handler.SectionTwoResults[path] = new() { ["customBusinessField"] = "NativeFile" };
            ModelHandler model = new(Completion(tool: "fetch", args: Fetch(path)), Completion("NativeFile found."));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("List"), default))!;
            Must(output.Contains("NativeFile found") && h.Handler.ToolCalls == 1 && h.Handler.Calls.Single().Args.GetProperty("entityUrls")[0].GetString() == path, output);
        });
        await check("Direct expired read session is not replayed", async () =>
        {
            await using Harness h = new(); DirectEnable(h); h.Handler.Expirations = 1;
            ModelHandler model = new(Completion(tool: "fetch", args: Fetch(Team)));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Read"), default))!;
            Must(output.Contains("failed") && h.Handler.ToolCalls == 1, output);
        });
        await check("Direct native CDDL and plain text use only selected operations", async () =>
        {
            await using Harness h = new(); DirectEnable(h); h.Handler.BlobAsText = true;
            h.Handler.DiscoveryMcpResult = new() { ["content"] = new JsonArray(new JsonObject
                { ["type"] = "text", ["text"] = "native = { customBusinessField: tstr }" }) };
            ModelHandler model = new(
                Completion(tool: "get_schema", args: new { path = "/me/drive", operationType = "fetch", format = "cddl" }, id: "schema"),
                Completion(tool: "fetch_blob", args: new { path = "/drives/drive/items/file/content", format = (string?)null }, id: "text"),
                Completion("Text read."));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Read text"), default))!;
            Must(output.Contains("Text read") && h.Handler.ToolCalls == 2 && model.Requests[1].GetRawText().Contains("customBusinessField") &&
                model.Requests[2].GetRawText().Contains("decodedPlainText"), output);
        });
        await check("Direct native supplied fields retained with explicit binary-field omission", async () =>
        {
            await using Harness h = new(); DirectEnable(h); h.Handler.SectionTwoText = true;
            h.Handler.SectionTwoResults[Team] = new() { ["customBusinessField"] = "KEEP-FIELD", ["accessToken"] = "PRIVATEsecret",
                ["webUrl"] = "https://never.invalid/?sig=PRIVATE", ["base64Content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("PRIVATEbinary")) };
            ModelHandler model = new(Completion(tool: "fetch", args: Fetch(Team)), Completion("Result."));
            await App(h, model, DirectOptions()).Handle(Harness.Activity("Read"), default);
            string request = model.Requests[1].GetRawText();
            Must(request.Contains("KEEP-FIELD") && request.Contains("PRIVATEsecret") && request.Contains("https://never.invalid") &&
                request.Contains("binary field omitted") && !request.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("PRIVATEbinary"))));
        });
        foreach (string tool in new[] { "ask", "mcp-confirm", "unadvertised" })
            await check("Direct removal of approval does not expand tool surface " + tool, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                ModelHandler model = new(Completion(tool: tool, args: new { query = "Do it" }));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Operate"), default))!;
                Must(output.Contains("unavailable direct tool") && h.Handler.ToolCalls == 0, output);
            });
        foreach (string body in new[] { "{\"headers\":{\"Authorization\":\"PRIVATE\"}}", "{\"overwrite\":false,\"overwrite\":true}" })
            await check("Direct immediate writes retain override and duplicate-body guards", async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                ModelHandler model = new(Completion(tool: "do_action", args: new { actionUrl = "/native/action", jsonBody = body }));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Operate"), default))!;
                Must(output.Contains("denied") && h.Handler.ToolCalls == 0 && !output.Contains("PRIVATE"), output);
            });
        await check("Direct exact model and call timings include immediate write", async () =>
        {
            await using Harness h = new(); DirectEnable(h); TimingClock clock = new();
            h.Handler.OnMutation = () => clock.Advance(50);
            ModelHandler model = new(Completion(tool: "create_entity", args: CreateArgs()), Completion("Done."));
            model.BeforeResponse = () => clock.Advance(model.Requests.Count * 100);
            string output = (await App(h, model, DirectOptions(), clock).Handle(Harness.Activity("Create"), default))!;
            Must(output.Contains("Total processing this response: 350.0 ms") &&
                output.Contains("LLM tool-selection: 100.0 ms") && output.Contains("final-answer generation: 200.0 ms") &&
                !output.Contains("approval waiting"), output);
        });
        await check("Direct cancellation before model result prevents writes", async () =>
        {
            await using Harness h = new(); DirectEnable(h); using CancellationTokenSource cancel = new();
            ModelHandler model = new(Completion(tool: "create_entity", args: CreateArgs())) { BeforeResponse = cancel.Cancel };
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Create"), cancel.Token))!;
            Must(output.Contains("cancelled") && h.Handler.Mutations == 0, output);
        });
        await check("Direct model budget stops after actual write without replay", async () =>
        {
            await using Harness h = new(); DirectEnable(h); NaturalLanguageOptions options = DirectOptions(); options.Budgets.ModelTurns = 1;
            ModelHandler model = new(Completion(tool: "create_entity", args: CreateArgs()));
            string output = (await App(h, model, options).Handle(Harness.Activity("Create"), default))!;
            Must(output.Contains("ModelTurns") && h.Handler.Mutations == 1 && model.Requests.Count == 1, output);
        });
        await check("Direct mode retires slash preview and confirm without model or native dispatch", async () =>
        {
            await using Harness h = new(); DirectEnable(h); ModelHandler model = new();
            WorkIqIngress app = App(h, model, DirectOptions());
            string preview = (await app.Handle(Harness.Activity("/folder create drive root SlashFolder"), default))!;
            Must(preview == WorkIqIngress.Retired && h.Handler.Mutations == 0 && model.Requests.Count == 0, preview);
            string result = (await app.Handle(Harness.Activity("/confirm old-id"), default))!;
            Must(result == WorkIqIngress.Retired && h.Handler.ToolCalls == 0 && model.Requests.Count == 0, result);
        });
        await check("Direct disabled cannot fall back to a guarded read-only flow", async () =>
        {
            await using Harness h = new(); Enable(h);
            ModelHandler model = new(Completion(tool: "create_entity", args: CreateArgs()));
            string result = (await App(h, model, Options()).Handle(Harness.Activity("Create folder"), default))!;
            Must(result == NaturalLanguageOptions.WorkIqActivation && h.Handler.ToolCalls == 0 &&
                h.Handler.CatalogCalls == 0 && model.Requests.Count == 0, result);
        });
        await check("Direct native large schema fits without escaped string inflation", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            string schema = "native = { fields: [" + string.Join(", ", Enumerable.Repeat("\"field-value\"", 5500)) + "] }";
            h.Handler.DiscoveryMcpResult = new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = schema }) };
            ModelHandler model = new(Completion(tool: "get_schema", args: new { path = "/me/drive", operationType = "fetch", format = "cddl" }), Completion("Schema returned."));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Schema"), default))!;
            Must(output.Contains("Schema returned.") && model.Requests.Count == 2, output);
        });
        await check("Direct session derives unknown-effect failure handling without caller mutation flag", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            WorkIqTool custom = (await h.AuSession.ListAsync(default)).Tools.Single(t => t.Name == "custom_operation");
            h.Handler.MutationTimeout = true;
            try
            {
                await h.AuSession.CallDirectAsync(custom, new() { ["jsonBody"] = "{}" }, default);
                throw new Exception("Expected unknown outcome.");
            }
            catch (UnknownOutcomeException) { }
            Must(h.Handler.ToolCalls == 1 && h.Handler.Mutations == 1);
        });
    }
}
