using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static async Task ContentFidelityChecks(Func<string, Func<Task>, Task> check)
    {
        await check("Native shared model ingress and final text retain original spelling", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            string input = " \nExplain only: <at id=\"0\">R&amp;D</at> " + new string('q', 116) +
                """ {"unicode":"\u03b1"} access_token=SYNTHETIC """ + "\n ";
            const string answer = "<at id=\"0\">R&amp;D</at> @here https://example.invalid/?sig=SUPPLIED";
            ModelHandler model = new(Completion(answer));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(input), default))!;
            Must(model.Requests.Single().GetProperty("messages").EnumerateArray().Any(m =>
                m.GetProperty("role").GetString() == "user" && m.GetProperty("content").GetString() == input) &&
                output.Contains(answer) && h.Handler.ToolCalls == 0 && !BotText.Html(output).Contains("<at "));
        });
        foreach (bool nestedType in new[] { false, true })
        foreach (string? effort in new string?[] { null, "low" })
            await check("SDK generation options and generated annotations are not changed by client " + nestedType + effort, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                string body = "{\"@odata.type\":\"#microsoft.graph.chatMessage\",\"body\":{" +
                    (nestedType ? "\"@odata.type\":\"#microsoft.graph.itemBody\"," : "") +
                    "\"contentType\":\"text\",\"content\":\"Hi\"}}";
                NaturalLanguageOptions options = DirectOptions(); options.ReasoningEffort = effort;
                ModelHandler model = new(Completion(tool: "create_entity",
                    args: new { parentUrl = Channel + "/messages", jsonBody = body }), Completion("Synthetic only."));
                await App(h, model, options).Handle(Harness.Activity("Post synthetic Hi"), default);
                JsonElement request = model.Requests[0];
                Must(request.GetProperty("model").GetString() == options.Deployment);
                foreach (string name in new[] { "temperature", "top_p", "seed", "tool_choice", "parallel_tool_calls", "store" })
                    Must(!request.TryGetProperty(name, out _), "Unexpected configured generation field " + name);
                Must(effort is null ? !request.TryGetProperty("reasoning_effort", out _) :
                    request.GetProperty("reasoning_effort").GetString() == effort);
                string[] offered = request.GetProperty("tools").EnumerateArray().Select(t =>
                {
                    JsonElement function = t.GetProperty("function");
                    Must(!function.TryGetProperty("strict", out _));
                    return function.GetProperty("name").GetString()!;
                }).ToArray();
                string[] catalogOrder = h.Handler.CatalogTools!.OfType<JsonObject>().Select(t => t["name"]!.GetValue<string>())
                    .Where(n => n != "ask").Append(WorkIqSkill.LoadTool).Append(DirectContinuationHandles.ToolName).ToArray();
                Must(offered.SequenceEqual(catalogOrder));
                Must(h.Handler.Calls.Single().Args.GetProperty("jsonBody").GetString() == body && model.Requests.Count == 2);
                Must(model.Requests[1].GetProperty("messages").EnumerateArray().Any(m =>
                    m.GetProperty("role").GetString() == "assistant" && m.TryGetProperty("tool_calls", out _)));
            });
        await check("Repeated direct requests reuse MCP transport but never model transcript or cached catalog", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            JsonElement[] firstRequests = new JsonElement[2];
            var activity = Harness.Activity("Same synthetic request");
            for (int n = 0; n < 2; n++)
            {
                ModelHandler model = new(Completion("Synthetic answer " + n));
                await App(h, model, DirectOptions()).Handle(activity, default);
                firstRequests[n] = model.Requests.Single();
                Must(firstRequests[n].GetProperty("messages").GetArrayLength() == 3 &&
                    !firstRequests[n].GetRawText().Contains("Synthetic answer"));
            }
            Must(firstRequests[0].GetRawText() == firstRequests[1].GetRawText() && h.Handler.CatalogCalls == 2 &&
                h.Handler.ToolCalls == 0);
        });
        foreach (string profile in new[] { "AgentUser", "SignedInHuman", "PrivateHuman" })
        foreach (bool encodedPath in new[] { false, true })
        foreach (bool encodedResult in new[] { false, true })
            await check("Pasted opaque tag exact input through SDK model native wire and default trace " +
                profile + encodedPath + encodedResult, async () =>
            {
                string tag = string.Concat(Enumerable.Repeat("Ab0_-xyz", 14)) + "WXYZ";
                Must(tag.Length == 116);
                string path = Channel + "/messages";
                if (encodedPath) path = path.Replace(":", "%3A", StringComparison.Ordinal).Replace("@", "%40", StringComparison.Ordinal);
                string body = """
                    { "body" : { "contentType" : "html", "content" : "Hi <at id=\"0\">R&amp;D</at> &#39;quoted&#39; \u03b1 @here\n access_token=SYNTHETIC-SUPPLIED" },
                      "mentions" : [ { "id":0, "mentionText":"R&D", "mentioned":{"tag":{"id":"TAG_ID","displayName":"R&D"}} } ] }
                    """.Replace("TAG_ID", tag, StringComparison.Ordinal);
                string args = "{ \"parentUrl\" : " + JsonSerializer.Serialize(path) +
                    ", \"jsonBody\" : " + JsonSerializer.Serialize(body) + " }";
                string input = " \n" + args + "\n ";
                const string final = "Synthetic only: <at id=\"0\">R&amp;D</at> @here https://example.invalid/?sig=SUPPLIED";
                JsonObject selection = RawArgumentCompletion("create_entity", "{}");
                ModelHandler model = new(Completion(tool: "fetch", args: Fetch(Team + "/tags"), id: "evidence"),
                    selection, Completion(final));
                await using PrivateHarness p = new(model);
                p.H.Handler.SectionTwoText = encodedResult;
                p.H.Handler.SectionTwoResults[Team + "/tags"] = new()
                {
                    ["value"] = new JsonArray(new JsonObject { ["id"] = tag, ["displayName"] = "R&D" }),
                    ["arbitraryBusinessField"] = "opaque " + tag + " [opaque value redacted]",
                    ["jsonText"] = "{\"escaped\":\"\\u03b1\",\"body\":\"&amp;\"}"
                };
                string? observedInput = null;
                model.BeforeResponse = () =>
                {
                    if (model.Requests.Count == 1)
                    {
                        observedInput = model.Requests[0].GetProperty("messages").EnumerateArray().Single(m =>
                            m.GetProperty("role").GetString() == "user" && m.GetProperty("content").GetString() == input)
                            .GetProperty("content").GetString();
                        Must(observedInput == input);
                        // Fake model selects exactly the arguments it actually received, not a second fixture copy.
                        selection["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"] = observedInput;
                    }
                    if (model.Requests.Count == 2)
                    {
                        string toolText = model.Requests[1].GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
                        JsonElement data = NativeData(toolText).GetProperty("results")[0].GetProperty("data");
                        Must(data.GetProperty("value")[0].GetProperty("id").GetString() == tag &&
                            data.GetProperty("arbitraryBusinessField").GetString() == "opaque " + tag + " [opaque value redacted]" &&
                            data.GetProperty("jsonText").GetString() == "{\"escaped\":\"\\u03b1\",\"body\":\"&amp;\"}");
                    }
                };
                string output;
                if (profile == "PrivateHuman")
                {
                    p.Origin.Text = input;
                    PrivateJob job = await p.Initiate();
                    Must(job.Request == input && job.ConsentScope == PrivateConsentScope.NativeReadWriteV1);
                    Must(p.Routing.Requests.Single().GetProperty("messages").EnumerateArray().Any(m =>
                        m.GetProperty("role").GetString() == "user" && m.GetProperty("content").GetString()!.Contains(input)));
                    await p.Authenticate(await p.Approve(job)); await p.Worker.RunOne(default);
                    PrivateJob done = p.Store.Get(job.Id);
                    Must(done.Request == input && done.State == PrivateJobState.Completed);
                    output = done.Result!;
                    string delivery = string.Join("\n", p.Ui.Wire.Requests.Select(r => r.Body.GetRawText()));
                    Must(!delivery.Contains("synthetic-model-key") && !delivery.Contains("synthetic-human-secret"));
                }
                else
                {
                    if (profile == "SignedInHuman") await p.H.Connect(Harness.Human);
                    output = (await App(p.H, model, DirectOptions(profile)).Handle(Harness.Activity(input, "personal"), default))!;
                }
                JsonElement wire = p.H.Handler.WireToolRequests.Single(r =>
                    r.GetProperty("params").GetProperty("name").GetString() == "create_entity")
                    .GetProperty("params").GetProperty("arguments");
                Must(observedInput == input && wire.GetProperty("parentUrl").GetString() == path &&
                    wire.GetProperty("jsonBody").GetString() == body &&
                    Encoding.UTF8.GetBytes(wire.GetProperty("jsonBody").GetString()!).SequenceEqual(Encoding.UTF8.GetBytes(body)));
                Must(p.H.Handler.Mutations == 1 && p.H.Handler.ToolCalls == 2 && model.Requests.Count == 3 &&
                    output.Contains(tag) && output.Contains("SYNTHETIC-SUPPLIED") && output.Contains(final));
                Must(output.Contains(JsonSerializer.Serialize(wire)) && !body.Contains("@odata.type"));
                string inert = BotText.Html(output);
                Must(!inert.Contains("<at ") && inert.Contains("&lt;at "));
                foreach (string token in p.H.Handler.Bearers)
                {
                    Must(!model.Requests.Any(r => r.GetRawText().Contains(token)) && !output.Contains(token));
                    if (profile == "AgentUser") AgentUserTokenProvider.ValidateTokenShape(token, p.H.Settings);
                    else p.H.HumanSettings.ValidateAccessToken(token, p.H.HumanSettings.Key(
                        p.H.Policy.Authorize(p.Personal, p.H.Settings)), Harness.Principal(Harness.Human));
                }
                Must(!model.Requests.Any(r => r.GetRawText().Contains("synthetic-model-key") ||
                    r.GetRawText().Contains("synthetic-human-secret")));
            });
        await check("Literal old redaction markers and unknown opaque text are not repaired or rescrubbed", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            const string input = """{"parentUrl":"/chats/[opaque value redacted]/messages","jsonBody":"{\"body\":{\"contentType\":\"html\",\"content\":\"<at id=\\\"0\\\">&amp;</at>\"}}"}""";
            ModelHandler model = new(RawArgumentCompletion("create_entity", input), Completion("Synthetic only."));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(input), default))!;
            Must(model.Requests[0].GetProperty("messages").EnumerateArray().Any(m =>
                m.GetProperty("role").GetString() == "user" && m.GetProperty("content").GetString() == input));
            using JsonDocument supplied = JsonDocument.Parse(input);
            JsonElement dispatched = h.Handler.Calls.Single().Args;
            Must(dispatched.GetProperty("parentUrl").GetString() == supplied.RootElement.GetProperty("parentUrl").GetString() &&
                dispatched.GetProperty("jsonBody").GetString() == supplied.RootElement.GetProperty("jsonBody").GetString() &&
                output.Contains("pathContainsRedactionMarker=True"));
        });
        foreach (bool encoded in new[] { false, true })
            await check("Native output duplicate property rejection survives content fidelity " + encoded, async () =>
            {
                const string duplicate = """{"opaqueId":"first","opaqueId":"second"}""";
                using JsonDocument doc = JsonDocument.Parse(duplicate);
                ToolReply reply = new(new CallToolResult
                {
                    StructuredContent = encoded ? null : doc.RootElement.Clone(),
                    Content = encoded ? [new TextContentBlock { Text = JsonSerializer.Serialize(duplicate) }] : []
                }, new("fixture", "reused", 0, 0, 0, 0, 0, 0));
                await Denied(() => { DirectMcpPresentation.Result(reply, DirectOptions().ResolveBudgets()); return Task.CompletedTask; });
            });
        await check("Structured native content never hides a supplied companion text block", () =>
        {
            const string companion = "Original &amp; <at> @label\n" + "access_token=SYNTHETIC";
            ToolReply reply = new(new CallToolResult
            {
                StructuredContent = Json(new { id = new string('q', 116) }),
                Content = [new TextContentBlock { Text = companion }]
            }, new("fixture", "reused", 0, 0, 0, 0, 0, 0));
            string output = DirectMcpPresentation.Result(reply, DirectOptions().ResolveBudgets());
            Must(output.Contains(companion) && output.Contains(new string('q', 116)));
            return Task.CompletedTask;
        });
        await check("Native binary schema definitions and similarly named business text remain original", () =>
        {
            JsonObject schema = new()
            {
                ["type"] = "object", ["base64Notation"] = "not a binary payload",
                ["properties"] = new JsonObject
                {
                    ["base64Content"] = new JsonObject { ["type"] = "string", ["description"] = "Native upload argument" },
                    ["bytes"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } }
                }
            };
            string text = DirectMcpPresentation.Result(NativeReply(schema), DirectOptions().ResolveBudgets());
            Must(text.Split('\n', 2)[1] == Json(schema).GetRawText());
            return Task.CompletedTask;
        });
        await check("Default argument display is bounded explicit omission not false complete capture", () =>
        {
            JsonElement args = Json(new { parentUrl = Channel + "/messages", jsonBody = "{\"content\":\"" + new string('x', 5000) + "\"}" });
            string display = DirectMcpPresentation.Arguments(args, 1024);
            Must(display.Contains("omitted") && display.Contains("not a complete capture") && !display.Contains(new string('x', 100)));
            return Task.CompletedTask;
        });
    }
}
