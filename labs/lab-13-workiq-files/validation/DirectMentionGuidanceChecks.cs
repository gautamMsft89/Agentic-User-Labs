using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static async Task DirectMentionGuidanceChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (bool human in new[] { false, true })
        foreach (bool encoded in new[] { false, true })
        foreach (string prefix in new[] { "/chats/", "/me/chats/" })
            await check("Exact pasted chat request survives ingress model and native wire " + human + encoded + prefix, async () =>
            {
                string path = prefix + (encoded ? Uri.EscapeDataString(NativePersonalChat) : NativePersonalChat) + "/messages";
                string body = """{"body":{"contentType":"html","content":"Hi <at id=\"0\">Example &amp; Person</at>"},"mentions":[{"id":0,"mentionText":"Example & Person","mentioned":{"user":{"id":"synthetic-user","displayName":"Example & Person","userIdentityType":"aadUser"}}}]}""";
                string input = JsonSerializer.Serialize(new { parentUrl = path, jsonBody = body },
                    new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                Must(DirectMcpPresentation.Text(input, 16000) == input);
                ModelHandler model = new(Completion(tool: "create_entity", args: new { parentUrl = path, jsonBody = body }),
                    Completion("Synthetic only."));
                await using PrivateHarness p = new(model);
                model.BeforeResponse = () =>
                {
                    if (model.Requests.Count != 1) return;
                    Must(model.Requests[0].GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() == input);
                };
                if (human)
                {
                    p.Origin.Text = input;
                    PrivateJob job = await p.Initiate();
                    Must(job.Request == input);
                    await p.Authenticate(await p.Approve(job)); await p.Worker.RunOne(default);
                }
                else await App(p.H, model, DirectOptions()).Handle(Harness.Activity(input), default);
                Must(p.H.Handler.Calls.Single().Args.GetProperty("parentUrl").GetString() == path &&
                    p.H.Handler.Calls.Single().Args.GetProperty("jsonBody").GetString() == body);
                string facts = DirectMcpPresentation.PathEvidence(p.H.Handler.Calls.Single().Args);
                Must(facts.Contains("pathContainsRedactionMarker=False"));
            });
        await check("Pasted supplied content is passed verbatim without business-ID exceptions", () =>
        {
            string path = "/me/chats/" + NativePersonalChat + "/messages";
            foreach (string input in new[] { "access_token=" + path, "https://example.invalid" + path,
                JsonSerializer.Serialize(new { token = path }), "Bearer " + path, "api_key=" + path,
                new string('Q', 116), NativePersonalChat, "/chats/" + new string('Q', 116) + "/messages",
                "/unknown/" + NativePersonalChat, "/chats/" + NativePersonalChat + "?sig=PRIVATE",
                "/chats/" + NativePersonalChat + "TRAILING" })
                Must(DirectMcpPresentation.UserInput(input, 16000) == input);
            Must(DirectMcpPresentation.UserInput("Inspect " + path, 16000) == "Inspect " + path);
            string facts = DirectMcpPresentation.PathEvidence(Json(new { parentUrl = "/chats/19:%5Bopaque%20value%20redacted%5D@unq.gbl.spaces/messages" }));
            Must(facts.Contains("pathContainsRedactionMarker=True") && !facts.Contains("19:"));
            return Task.CompletedTask;
        });
        await check("Pasted HTML and escaped JSON preserve original spelling after decoded credential inspection", () =>
        {
            Must(DirectMcpPresentation.UserInput("Hi <at id=\"0\">Example</at>", 16000) == "Hi <at id=\"0\">Example</at>");
            Must(DirectMcpPresentation.UserInput("Example &amp; Person", 16000) == "Example &amp; Person");
            Must(DirectMcpPresentation.UserInput("Literal &amp;lt; and &amp;amp;", 16000) == "Literal &amp;lt; and &amp;amp;");
            Must(DirectMcpPresentation.UserInput("&quot;access_token&quot;: &quot;PRIVATE&quot;", 16000) ==
                "&quot;access_token&quot;: &quot;PRIVATE&quot;");
            return Task.CompletedTask;
        });
        foreach (string prefix in new[] { "/chats/", "/me/chats/" })
        foreach (bool declared in new[] { false, true })
        foreach (bool validHtml in new[] { false, true })
            await check("Chat mention failure preserves path and omitted or declared identity type " + prefix + declared + validHtml, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                h.Handler.ToolError = new() { ["results"] = new JsonArray(new JsonObject
                    { ["statusCode"] = 400, ["error"] = new JsonObject { ["code"] = "BadRequest", ["message"] = "PRIVATE unclassified detail" } }) };
                JsonObject user = new() { ["id"] = Harness.Human, ["displayName"] = "PRIVATE-name" };
                if (declared) user["userIdentityType"] = "aadUser";
                string body = new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.chatMessage",
                    ["body"] = new JsonObject { ["@odata.type"] = "#microsoft.graph.itemBody", ["contentType"] = "html",
                        ["content"] = validHtml ? "Hi <at id=\"0\">PRIVATE-name</at>" : "Hi <at id=\\\"0\\\">PRIVATE-name</at>" },
                    ["mentions"] = new JsonArray(new JsonObject { ["id"] = 0, ["mentionText"] = "PRIVATE-name",
                        ["mentioned"] = new JsonObject { ["user"] = user } })
                }.ToJsonString();
                string path = prefix + Uri.EscapeDataString(NativePersonalChat) + "/messages";
                ModelHandler model = new(Completion(tool: "create_entity", args: new { parentUrl = path, jsonBody = body }));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Synthetic person mention"), default))!;
                Must(output.Contains("userIdentityTypeSupplied=" + declared) &&
                    output.Contains("userIdentityTypeAadUser=" + declared) &&
                    output.Contains("simpleHtmlIndexMatch=" + validHtml) && output.Contains("target=User") &&
                    output.Contains("providerReason[unclassified]") && output.Contains("bodyKind=Object; contentKind=String") &&
                    h.Handler.ToolCalls == 1 && model.Requests.Count == 1 &&
                    h.Handler.Calls.Single().Args.GetProperty("parentUrl").GetString() == path &&
                    h.Handler.Calls.Single().Args.GetProperty("jsonBody").GetString() == body, output);
                string diagnostic = DirectMentionDiagnostic.Describe("create_entity", h.Handler.Calls.Single().Args);
                Must(!diagnostic.Contains("PRIVATE") && !diagnostic.Contains(Harness.Human) && !diagnostic.Contains(NativePersonalChat));
                Must(DirectMentionDiagnostic.Describe("create_entity", Json(new { parentUrl = path + "/replies", jsonBody = body })) == "");
            });
        foreach (bool human in new[] { false, true })
            await check("Native user mention guidance and decoded body wire fidelity " + human, async () =>
            {
                string label = "Example & <Person>";
                string identity = Harness.Human;
                JsonObject target = new() { ["id"] = identity, ["displayName"] = label };
                target["userIdentityType"] = "aadUser";
                string html = "Hi <at id=\"0\">" + System.Net.WebUtility.HtmlEncode(label) + "</at>";
                string body = new JsonObject
                {
                    ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = html },
                    ["mentions"] = new JsonArray(new JsonObject { ["id"] = 0, ["mentionText"] = label,
                        ["mentioned"] = new JsonObject { ["user"] = target } })
                }.ToJsonString();
                string path = "/chats/" + Uri.EscapeDataString(NativePersonalChat) + "/messages";
                ModelHandler model = new(Completion(tool: "create_entity", args: new { parentUrl = path, jsonBody = body }),
                    Completion("Synthetic response only; rendering and notification unverified."));
                await using PrivateHarness p = new(model);
                if (human)
                {
                    p.Origin.Text = "As me, post a synthetic update with the requested native mention.";
                    PrivateJob job = await p.Initiate();
                    await p.Authenticate(await p.Approve(job));
                    await p.Worker.RunOne(default);
                    Must(job.ConsentScope == PrivateConsentScope.NativeReadWriteV1);
                }
                else await App(p.H, model, DirectOptions()).Handle(Harness.Activity("Post a synthetic native mention"), default);
                string system = model.Requests[0].GetProperty("messages")[0].GetProperty("content").GetString()!;
                Must(system.Contains(DirectMcpContract.MentionGuidance) &&
                    system.Contains("encode that JSON object as a string exactly once") &&
                    system.Contains("mentioned.user") && system.Contains("not the message sender") &&
                    system.Contains("native create_entity") && system.Contains("/chats/{resolved-existing-chat-id}/messages") &&
                    !system.Contains("send_user_mention") && !system.Contains("textBefore") && !system.Contains("textAfter") &&
                    !system.Contains("@microsoft.com") && !system.Contains("Sasidharan") &&
                    system.Contains("RESOLVED_USER_ID") && !system.Contains("RESOLVED_TAG_ID"));
                var call = p.H.Handler.Calls.Single();
                string[] offered = model.Requests[0].GetProperty("tools").EnumerateArray()
                    .Select(t => t.GetProperty("function").GetProperty("name").GetString()!).ToArray();
                Must(new[] { "create_entity", "fetch", "get_schema", "search_paths" }.All(offered.Contains) &&
                    !offered.Contains("send_user_mention"));
                Must(call.Tool == "create_entity" && call.Args.GetProperty("parentUrl").GetString() == path &&
                    call.Args.GetProperty("jsonBody").GetString() == body && p.H.Handler.Mutations == 1 && model.Requests.Count == 2);
                JsonElement wire = p.H.Handler.WireToolRequests.Single(r =>
                    r.GetProperty("params").GetProperty("name").GetString() == "create_entity").GetProperty("params").GetProperty("arguments");
                Must(wire.GetProperty("parentUrl").GetString() == path && wire.GetProperty("jsonBody").GetString() == body &&
                    !body.Contains("@odata.type"));
                using JsonDocument decoded = JsonDocument.Parse(call.Args.GetProperty("jsonBody").GetString()!);
                JsonElement root = decoded.RootElement, mention = root.GetProperty("mentions")[0];
                string content = root.GetProperty("body").GetProperty("content").GetString()!;
                Must(root.ValueKind == JsonValueKind.Object && content == html && !content.Contains("\\\"") &&
                    content.Contains("<at id=\"" + mention.GetProperty("id").GetInt32() + "\">") &&
                    System.Net.WebUtility.HtmlDecode(content.Split('>', 2)[1].Split("</at>", 2)[0]) ==
                        mention.GetProperty("mentionText").GetString() &&
                    mention.GetProperty("mentioned").GetProperty("user").GetProperty("id").GetString() == identity &&
                    mention.GetProperty("mentioned").GetProperty("user").GetProperty("userIdentityType").GetString() == "aadUser");
                foreach (string token in p.H.Handler.Bearers)
                    if (human)
                        p.H.HumanSettings.ValidateAccessToken(token, p.H.HumanSettings.Key(
                            p.H.Policy.Authorize(p.Personal, p.H.Settings)), Harness.Principal(Harness.Human));
                    else AgentUserTokenProvider.ValidateTokenShape(token, p.H.Settings);
            });
        foreach (bool human in new[] { false, true })
            await check("Removed local mention operation is unavailable without native dispatch " + human, async () =>
            {
                ModelHandler model = new(Completion(tool: "send_user_mention", args: new { }));
                await using PrivateHarness p = new(model);
                string output;
                if (human)
                {
                    PrivateJob job = await p.Initiate();
                    await p.Authenticate(await p.Approve(job)); await p.Worker.RunOne(default);
                    output = p.Store.Get(job.Id).Result!;
                }
                else output = (await App(p.H, model, DirectOptions()).Handle(Harness.Activity("Mention a person"), default))!;
                Must(output.Contains("unavailable direct tool") && p.H.Handler.ToolCalls == 0 && p.H.Handler.Mutations == 0);
            });
    }
}
