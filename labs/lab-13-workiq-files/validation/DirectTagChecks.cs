using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static readonly string NativeTag = Convert.ToBase64String(Encoding.ASCII.GetBytes(
        "2432b57b-0abd-43db-aa7b-16eadd115d34##eb653f92-8373-4de6-bfec-5b4d216b6ade##97f62344-57dc-409c-88ad-c4af14158ff5"));
    private static JsonElement NativeData(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text.Split('\n', 2)[1]);
        JsonElement value = doc.RootElement.Clone();
        for (int n = 0; n < 8; n++)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                using JsonDocument inner = JsonDocument.Parse(value.GetString()!);
                value = inner.RootElement.Clone();
            }
            else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("content", out JsonElement blocks) &&
                blocks.ValueKind == JsonValueKind.Array && blocks.GetArrayLength() == 1)
                value = blocks[0].GetProperty("text");
            else return value;
        }
        throw new Exception("Synthetic envelope depth exceeded.");
    }
    private static async Task DirectTagChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (bool marker in new[] { false, true })
        foreach (bool matches in new[] { false, true })
            await check("Native mention facts remain value-free and do not repair body " + marker + matches, () =>
            {
                string body = JsonSerializer.Serialize(new
                {
                    body = new { contentType = "html", content = "PRIVATE <at id=\"0\">PRIVATE</at>" },
                    mentions = new[] { new { id = matches ? 0 : 1, mentioned = new { tag = new
                        { id = marker ? "[opaque value redacted]" : new string('Q', 116) } } } }
                });
                JsonElement args = Json(new { parentUrl = Channel + "/messages", jsonBody = body });
                string facts = DirectMentionDiagnostic.Describe("create_entity", args);
                Must(facts.Contains("tagIdRedactionMarker=" + marker) && facts.Contains("simpleHtmlIndexMatch=" + matches) &&
                    !facts.Contains("PRIVATE") && args.GetProperty("jsonBody").GetString() == body);
                return Task.CompletedTask;
            });
        foreach (var reason in new[]
        {
            ("Could not find a property named 'PRIVATE' on type 'PRIVATE'", "unsupported-property-reported"),
            ("A type named PRIVATE could not be resolved by the model", "invalid-type-reported"),
            ("Required property PRIVATE is missing", "missing-required-property-reported"),
            ("Mention index PRIVATE does not match body", "mention-binding-reported"),
            ("Insufficient privileges PRIVATE", "authorization-reported"),
            ("Resource not found for the segment PRIVATE", "invalid-path-reported"),
            ("PRIVATE arbitrary error", "unclassified")
        })
            await check("Protocol reason remains structured not raw transport data " + reason.Item2, () =>
            {
                string text = SafeWorkIqDiagnostic.ResourceFailures(NativeReply(new()
                { ["statusCode"] = 400, ["error"] = new JsonObject { ["code"] = "BadRequest", ["message"] = reason.Item1 } }).Result);
                Must(text.Contains(reason.Item2) && !text.Contains("PRIVATE"));
                return Task.CompletedTask;
            });
        foreach (string id in new[] { new string('Q', 116), string.Concat(Enumerable.Repeat("Ab-_", 29)),
            NativeTag, "tag:" + new string('z', 112), Harness.Team, "short-id", new string('x', 4096),
            "opaque+/with=padding-not-repaired", "unicode-\u03b1-tag", "[opaque value redacted]" })
        foreach (bool encoded in new[] { false, true })
            await check("Native tag value reaches model and native dispatch without provenance exemption " + id.Length + encoded, async () =>
            {
                await using Harness h = new(); DirectEnable(h); h.Handler.SectionTwoText = encoded;
                string path = Team + "/tags";
                h.Handler.SectionTwoResults[path] = new() { ["value"] = new JsonArray(new JsonObject { ["id"] = id, ["displayName"] = "On-Call" }) };
                string body = JsonSerializer.Serialize(new
                {
                    body = new { contentType = "html", content = "Hi <at id=\"0\">On-Call</at>" },
                    mentions = new[] { new { id = 0, mentionText = "On-Call", mentioned = new { tag = new { id, displayName = "On-Call" } } } }
                });
                ModelHandler model = new(Completion(tool: "fetch", args: Fetch(path), id: "fetch"),
                    Completion(tool: "create_entity", args: new { parentUrl = Channel + "/messages", jsonBody = body }, id: "create"),
                    Completion("Synthetic returned."));
                model.BeforeResponse = () =>
                {
                    if (model.Requests.Count != 2) return;
                    string result = model.Requests[1].GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
                    Must(NativeData(result).GetProperty("results")[0].GetProperty("data").GetProperty("value")[0].GetProperty("id").GetString() == id);
                };
                await App(h, model, DirectOptions()).Handle(Harness.Activity("Synthetic tag request"), default);
                Must(h.Handler.Calls.Count == 2 && h.Handler.Calls[1].Args.GetProperty("jsonBody").GetString() == body);
            });
        foreach (object? value in new object?[] { "", new string('x', 4097), "\nsynthetic", " ", 42, null,
            "https://example.invalid/?sig=synthetic", "Bearer synthetic", "access_token=synthetic" })
            await check("Native supplied tag fields preserved even when not valid tag identities " + (value?.ToString()?.Length ?? -1), () =>
            {
                JsonObject data = new() { ["value"] = new JsonArray(new JsonObject { ["id"] = JsonSerializer.SerializeToNode(value) }) };
                string output = DirectMcpPresentation.Result(NativeReply(data), DirectOptions().ResolveBudgets());
                Must(NativeData(output).GetRawText() == Json(data).GetRawText());
                return Task.CompletedTask;
            });
        foreach (bool target in new[] { false, true })
            await check("Native failed mention remains exact single write without repair " + target, async () =>
            {
                await using Harness h = new(); DirectEnable(h); h.Handler.MutationResourceStatus = 400;
                JsonObject mentioned = target ? new() { ["tag"] = new JsonObject { ["id"] = NativeTag, ["displayName"] = "On-Call" } } :
                    new() { ["conversation"] = null };
                string body = new JsonObject { ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = "Hi <at id=\"0\">On-Call</at>" },
                    ["mentions"] = new JsonArray(new JsonObject { ["id"] = 0, ["mentioned"] = mentioned }) }.ToJsonString();
                ModelHandler model = new(Completion(tool: "create_entity", args: new { parentUrl = Channel + "/messages", jsonBody = body }));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Synthetic tag failure"), default))!;
                Must(h.Handler.Calls.Single().Args.GetProperty("jsonBody").GetString() == body &&
                    h.Handler.Mutations == 1 && model.Requests.Count == 1 && output.Contains("status[400]"));
            });
    }
}
