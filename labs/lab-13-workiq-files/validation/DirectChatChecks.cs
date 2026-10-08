using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static readonly string NativePersonalChat = "19:41a22be4-b8c2-4f32-a3bd-12a345678901_51b33cf5-c9d3-4033-b4ce-23b456789012@unq.gbl.spaces";
    private static async Task DirectChatChecks(Func<string, Func<Task>, Task> check)
    {
        await check("Direct native create schema keeps required bind metadata and example URL", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.DiscoveryResult = new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("user@odata.bind"),
                ["properties"] = new JsonObject
                {
                    ["userId"] = new JsonObject { ["type"] = "string", ["readOnly"] = true },
                    ["user@odata.bind"] = new JsonObject { ["type"] = "string",
                        ["examples"] = new JsonArray("https://graph.microsoft.com/v1.0/users('" + Harness.Human + "')") }
                }
            };
            ModelHandler model = new(Completion(tool: "get_schema",
                args: new { path = "/chats", operationType = "create", format = "jsonschema" }), Completion("Schema inspected."));
            await App(h, model, DirectOptions()).Handle(Harness.Activity("Inspect the native create contract without creating anything"), default);
            string result = model.Requests[1].GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
            using JsonDocument doc = JsonDocument.Parse(result.Split('\n', 2)[1]);
            Must(doc.RootElement.GetProperty("required")[0].GetString() == "user@odata.bind" &&
                doc.RootElement.GetProperty("properties").GetProperty("userId").GetProperty("readOnly").GetBoolean() &&
                result.Contains("https://") && !result.Contains("[URL redacted]") &&
                h.Handler.ToolCalls == 1 && h.Handler.Mutations == 0);
        });
        foreach (string chat in new[] { NativePersonalChat, "19:" + new string('a', 72) + "@thread.v2",
            "19:" + new string('b', 64) + "@thread.tacv2" })
        foreach (bool encoded in new[] { false, true })
            await check("Direct native chat identity survives real SDK model and MCP serialization " + encoded, async () =>
            {
                await using Harness h = new(); DirectEnable(h); h.Handler.SectionTwoText = encoded;
                const string list = "/me/chats?$select=id,chatType&$top=50";
                string members = "/me/chats/" + Uri.EscapeDataString(chat) + "/members";
                h.Handler.SectionTwoResults[list] = new() { ["value"] = new JsonArray(new JsonObject { ["id"] = chat }) };
                h.Handler.SectionTwoResults[members] = new() { ["value"] = new JsonArray(new JsonObject { ["userId"] = Harness.Human }) };
                ModelHandler model = new(Completion(tool: "fetch", args: Fetch(list), id: "chats"),
                    Completion(tool: "fetch", args: Fetch(members), id: "members"), Completion("Native member lookup returned."));
                model.BeforeResponse = () =>
                {
                    if (model.Requests.Count != 2) return;
                    JsonElement message = model.Requests[1].GetProperty("messages").EnumerateArray().Last();
                    Must(message.GetProperty("tool_call_id").GetString() == "chats");
                    JsonElement data = NativeData(message.GetProperty("content").GetString()!);
                    Must(data.GetProperty("results")[0].GetProperty("data").GetProperty("value")[0].GetProperty("id").GetString() == chat);
                };
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Inspect the existing chat members"), default))!;
                Must(output.Contains("Native member lookup returned") && !output.Contains("opaque value redacted") &&
                    h.Handler.Calls[1].Args.GetProperty("entityUrls")[0].GetString() == members &&
                    h.Handler.ToolCalls == 2 && h.Handler.Mutations == 0, output);
                Must(DirectMcpPresentation.Text(chat, 16000) == chat);
            });
        await check("Direct native chat content no longer needs identifier exemptions", () =>
        {
            string opaque = new('Q', 88);
            string output = DirectMcpPresentation.Result(NativeReply(new()
            {
                ["id"] = NativePersonalChat, ["chatId"] = NativePersonalChat, ["conversationId"] = NativePersonalChat,
                ["headers"] = new JsonObject { ["id"] = NativePersonalChat },
                ["credential"] = new JsonObject { ["chatId"] = NativePersonalChat },
                ["accessToken"] = NativePersonalChat, ["bytes"] = NativePersonalChat,
                ["description"] = NativePersonalChat, ["unknown"] = opaque,
                ["encoded"] = JsonSerializer.Serialize(new { chatId = NativePersonalChat, secret = NativePersonalChat }),
                ["webUrl"] = "https://synthetic.invalid/" + NativePersonalChat + "?sig=PRIVATE",
                ["user@odata.bind"] = "https://graph.microsoft.com/v1.0/users('" + Harness.Human + "')"
            }), DirectOptions().ResolveBudgets());
            using JsonDocument doc = JsonDocument.Parse(output.Split('\n', 2)[1]);
            JsonElement data = doc.RootElement;
            foreach (string key in new[] { "id", "chatId", "conversationId" }) Must(data.GetProperty(key).GetString() == NativePersonalChat);
            foreach (string key in new[] { "headers", "credential", "accessToken", "description" })
                Must(data.GetProperty(key).GetRawText().Contains(NativePersonalChat));
            using JsonDocument encodedData = JsonDocument.Parse(data.GetProperty("encoded").GetString()!);
            Must(encodedData.RootElement.GetProperty("chatId").GetString() == NativePersonalChat &&
                output.Contains("https://") && output.Contains("PRIVATE") && output.Contains(opaque) &&
                data.GetProperty("bytes").GetString()!.Contains("omitted"));
            foreach (string malformed in new[] { "19:" + opaque + "@unq.gbl.spaces",
                NativePersonalChat + "\naccess_token: PRIVATE", "19:" + new string('z', 129) + "@thread.v2",
                "eyJ" + opaque + "." + opaque + "." + opaque })
            {
                string safe = DirectMcpPresentation.Result(NativeReply(new() { ["chatId"] = malformed }), DirectOptions().ResolveBudgets());
                Must(NativeData(safe).GetProperty("chatId").GetString() == malformed, safe);
            }
            return Task.CompletedTask;
        });
        foreach (string key in new[] { "entityUrls", "entityUrl", "parentUrl", "actionUrl", "path" })
            await check("Direct selected chat argument display preserves supplied URLs and queries " + key, () =>
            {
                foreach (string prefix in new[] { "/chats/", "/me/chats/" })
                foreach (string segment in new[] { NativePersonalChat, Uri.EscapeDataString(NativePersonalChat) })
                {
                    string path = prefix + segment + "/members?access_token=PRIVATE";
                    object args = key == "entityUrls" ? new Dictionary<string, object?> { [key] = new[] { path } } :
                        new Dictionary<string, object?> { [key] = path };
                    string trace = DirectMcpPresentation.Arguments(Json(args), 12000);
                    Must(trace.Contains(segment) && trace.Contains("PRIVATE"), trace);
                    string absolute = DirectMcpPresentation.Arguments(Json(new { path = "https://synthetic.invalid" + path }), 12000);
                    Must(absolute.Contains(segment) && absolute.Contains("PRIVATE"), absolute);
                }
                return Task.CompletedTask;
            });
        foreach (bool encoded in new[] { false, true })
            await check("Direct resource diagnostics keep bounded per-error status and allowlisted code only " + encoded, () =>
            {
                object errors = new { results = new object[]
                {
                    new { statusCode = 200, data = new { text = "PRIVATE-success", error = "PRIVATE-success-field" } },
                    new { statusCode = 403, error = new { code = "accessDenied", message = "PRIVATE-message https://synthetic.invalid/?token=PRIVATE" } },
                    new { statusCode = 406, data = new { error = new { code = "PRIVATE-code", innerError = new { token = "PRIVATE" } } } },
                    new { statusCode = 400, error = new { error = new { code = "BadRequest", message = "PRIVATE" } } }
                } };
                CallToolResult result = encoded ? new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(errors) }] } :
                    new() { StructuredContent = Json(errors), Content = [] };
                string summary = SafeWorkIqDiagnostic.ResourceFailures(result);
                Must(summary.Contains("resource-error[1] status[403] code[accessDenied]") &&
                    summary.Contains("resource-error[2] status[406] code[unavailable]") &&
                    summary.Contains("resource-error[3] status[400] code[BadRequest]") &&
                    !summary.Contains("PRIVATE") && !summary.Contains("200") && !summary.Contains("https://"), summary);
                return Task.CompletedTask;
            });
        await check("Direct failed native creation preserves reference body and safe status without message send or replay", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.ToolError = JsonSerializer.SerializeToNode(new { results = new[] { new { statusCode = 400,
                error = new { code = "BadRequest", message = "PRIVATE synthetic service explanation" } } } })!.AsObject();
            string bind = "https://graph.microsoft.com/v1.0/users('" + Harness.Human + "')";
            JsonObject member = new() { ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
                ["roles"] = new JsonArray(), ["user@odata.bind"] = bind };
            string body = JsonSerializer.Serialize(new JsonObject { ["chatType"] = "oneOnOne", ["members"] = new JsonArray(member) });
            ModelHandler model = new(Completion(tool: "create_entity", args: new { parentUrl = "/chats", jsonBody = body }),
                Completion(tool: "create_entity", args: new { parentUrl = "/chats/unused/messages", jsonBody = "{}" }));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Send a message"), default))!;
            Must(output.Contains("effects may be partial") && output.Contains("status[400] code[BadRequest]") &&
                !output.Contains("PRIVATE") && !output.Contains(bind) && h.Handler.ToolCalls == 1 && model.Requests.Count == 1, output);
            Must(h.Handler.Calls.Single().Args.GetProperty("jsonBody").GetString() == body,
                "Trace URL redaction must never mutate the actual native binding or add members.");
        });
        await check("Direct diagnostic bounds and freeform error remain explicit without raw content", () =>
        {
            CallToolResult result = new() { IsError = true, Content = [new TextContentBlock { Text = "PRIVATE secret" }] };
            Must(SafeWorkIqDiagnostic.ResourceFailures(result).StartsWith("resource status/code unavailable"));
            result.StructuredContent = Json(new { results = Enumerable.Range(0, 20).Select(_ => new { statusCode = 403, error = new { code = "accessDenied" } }) });
            string bounded = SafeWorkIqDiagnostic.ResourceFailures(result);
            Must(bounded.Contains("diagnostic detail bound reached") && !bounded.Contains("resource-error[9]") && !bounded.Contains("PRIVATE"));
            return Task.CompletedTask;
        });
    }
}
