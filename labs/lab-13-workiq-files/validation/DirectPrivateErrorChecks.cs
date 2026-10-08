using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private const string ReturnedReason = "SYNTHETIC provider rejected this continuation parameter.";
    private static JsonObject ReturnedError() => new()
    {
        ["statusCode"] = 400,
        ["data"] = new JsonObject { ["error"] = new JsonObject
        {
            ["code"] = "SyntheticCursorError", ["message"] = ReturnedReason,
            ["innerError"] = new JsonObject { ["request-id"] = "12345678-1234-1234-1234-123456789012",
                ["client-request-id"] = "22345678-1234-1234-1234-123456789012",
                ["message"] = "Synthetic nested detail." }
        }}
    };
    private static string PrivateErrorPart(string output) => output[output.IndexOf(DirectPrivateErrorReceipt.Marker, StringComparison.Ordinal)..];
    private static async Task DirectPrivateErrorChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (bool text in new[] { false, true })
        foreach (string audience in new[] { "personal", "channel", "groupChat", "unknown", "personal-group-flag", "changed" })
            await check("Installed returned error receipt private-only unchanged native/model data " + audience + " text=" + text, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                var activity = Harness.Activity("Read this metadata; explain privately.", audience is "personal-group-flag" or "changed" ? "personal" : audience);
                if (audience == "personal-group-flag") activity.Conversation!.IsGroup = true;
                h.Handler.SectionTwoText = text;
                h.Handler.SectionTwoResource = _ => ReturnedError();
                ModelHandler model = new(Completion(tool: "fetch", args: Fetch("/me/drive"), id: "one"),
                    Completion("Only observed partial data; model paraphrase is not provider evidence."));
                if (audience == "changed") model.BeforeResponse = () =>
                { if (model.Requests.Count == 2) activity.Conversation!.IsGroup = true; };
                string output = (await App(h, model, DirectOptions()).Handle(activity, default))!;
                Must(output.Contains(DirectPrivateErrorReceipt.Marker) == (audience == "personal"), output);
                if (audience == "personal")
                {
                    string block = PrivateErrorPart(output);
                    Must(block.Contains(ReturnedReason) && block.Contains("SyntheticCursorError") &&
                        block.Contains("12345678-1234-1234-1234-123456789012") &&
                        block.Contains("results[1]") && block.Contains("matched-request") &&
                        block.Contains("NOT full raw HTTP"), block);
                }
                if (h.Handler.ToolCalls > 0)
                {
                    Must(h.Handler.ToolCalls == 1 && h.Handler.Mutations == 0);
                    Must(h.Handler.WireToolRequests.Single().GetProperty("params").GetProperty("arguments").GetRawText() ==
                        JsonSerializer.SerializeToElement(Fetch("/me/drive")).GetRawText());
                    Must(model.Requests.Count == 2);
                    Must(model.Requests[1].GetProperty("messages").EnumerateArray().Any(m =>
                        m.GetProperty("role").GetString() == "tool" && m.GetProperty("content").GetString()!.Contains(ReturnedReason)));
                }
                foreach (var request in model.Requests) Must(!request.GetRawText().Contains("Private WorkIQ returned-error excerpt"));
            });

        foreach (string shape in new[] { "mixed", "unattributed", "conflict", "success-decoy", "body-decoy", "missing",
            "duplicate", "malformed", "protocol-text", "mirrored", "oversize", "nested", "encoded-error", "plain-data", "malicious" })
            await check("Returned error bounded allowlist protocol evidence " + shape, () =>
            {
                DirectPrivateErrorReceipt receipt = new();
                JsonObject error = ReturnedError();
                error["entityUrl"] = "/failed";
                JsonObject success = new() { ["statusCode"] = 200, ["data"] = new JsonObject
                    { ["error"] = new JsonObject { ["message"] = "SUCCESS-BODY-DECOY" }, ["body"] = ReturnedReason } };
                JsonObject envelope = new() { ["results"] = new JsonArray(success, error) };
                CallToolResult result = PageResult(envelope);
                if (shape == "unattributed") error.Remove("entityUrl");
                if (shape == "conflict") error["url"] = "/successful";
                if (shape == "success-decoy") result = PageResult(success);
                if (shape == "body-decoy") result = PageResult(new() { ["value"] = new JsonArray(error.DeepClone()) });
                if (shape == "missing") result = PageResult(new() { ["statusCode"] = 400 });
                if (shape == "nested") result = PageResult(new() { ["statusCode"] = 400,
                    ["data"] = """{"error":{"code":"nested","message":"Nested JSON reason."}}""" });
                if (shape == "encoded-error") result = PageResult(new() { ["statusCode"] = 400,
                    ["error"] = """{"code":"nested","message":"Nested JSON reason.","successfulBody":"BUSINESS-SECRET","password":"PASSWORD-SECRET"}""" });
                if (shape == "plain-data") result = PageResult(new() { ["statusCode"] = 400, ["data"] = "Provider error text only." });
                if (shape == "duplicate") result = new() { StructuredContent = Json("""{"statusCode":400,"error":{"message":"ONE","message":"TWO"}}"""), Content = [] };
                if (shape == "malformed") result = new() { Content = [new TextContentBlock { Text = "{invalid" }] };
                if (shape == "protocol-text") result = new() { IsError = true, Content = [new TextContentBlock { Text = "Synthetic MCP failed, not a resource status." }] };
                if (shape == "mirrored") result.Content = [new TextContentBlock { Text = envelope.ToJsonString() }];
                if (shape == "oversize") result = new() { IsError = true, Content = [new TextContentBlock { Text = new string('x', 270000) }] };
                if (shape == "malicious")
                {
                    error["data"]!["error"]!["message"] = "Useful reason.\nAuthorization: Bearer BEARER-SECRET\napi_key=KEY-SECRET\n" +
                        "https://private.invalid/path?sig=URL-SECRET\n<at id=\"0\">Evil</at> [click](javascript:evil) @everyone `code`\n" +
                        "access_token=TOKEN-SECRET\nCookie: COOKIE-SECRET";
                    error["headers"] = new JsonObject { ["Set-Cookie"] = "HEADER-SECRET", ["request-id"] = "33345678-1234-1234-1234-123456789012" };
                    error["data"]!["error"]!["password"] = "PASSWORD-SECRET";
                }
                if (shape is "unattributed" or "conflict" or "malicious") result = PageResult(envelope);
                string original = JsonSerializer.Serialize(result);
                receipt.Observe(4, PageArgs("/successful", "/failed"), result, shape is not ("success-decoy" or "body-decoy"));
                string output = receipt.Receipt();
                Must(JsonSerializer.Serialize(result) == original && !output.Contains("SUCCESS-BODY-DECOY") && !output.Contains("/failed"));
                if (shape is "mixed" or "unattributed" or "conflict" or "mirrored")
                {
                    Must(output.Contains(ReturnedReason) && output.Contains("\"resultItem\":2"), output);
                    Must(output.Contains(shape == "unattributed" ? "unavailable-not-inferred-from-position" :
                        shape == "conflict" ? "conflicting" : "matched-request"), output);
                }
                if (shape is "success-decoy" or "body-decoy") Must(output == "", output);
                if (shape == "duplicate") Must(output.Contains("duplicate-fields-unavailable") && !output.Contains("ONE") && !output.Contains("TWO"));
                if (shape is "missing" or "malformed") Must(output.Contains("unavailable"), output);
                if (shape == "protocol-text") Must(output.Contains("MCP isError text") && !output.Contains("\"status\":\"400\""));
                if (shape == "mirrored") Must(output.Contains("duplicate-error-copy-omitted"));
                if (shape == "oversize") Must(output.Contains("excerptLimitReached=true") && output.Length < 5500);
                if (shape is "nested" or "encoded-error") Must(output.Contains("Nested JSON reason.") &&
                    !output.Contains("BUSINESS-SECRET") && !output.Contains("PASSWORD-SECRET"));
                if (shape == "plain-data") Must(output.Contains("Provider error text only."));
                if (shape == "malicious")
                {
                    Must(output.Contains("Useful reason.") && output.Contains("redacted") &&
                        output.Contains("33345678-1234-1234-1234-123456789012"), output);
                    foreach (string forbidden in new[] { "BEARER-SECRET", "KEY-SECRET", "URL-SECRET", "TOKEN-SECRET",
                        "COOKIE-SECRET", "HEADER-SECRET", "PASSWORD-SECRET", "<at", "[click]", "@everyone", "`code`" })
                        Must(!output.Contains(forbidden), output);
                }
                return Task.CompletedTask;
            });

        await check("Returned-error local exception and concurrent run isolation bounded output", async () =>
        {
            async Task<string> Capture(string message)
            {
                DirectPrivateErrorReceipt receipt = new(); await Task.Yield();
                for (int i = 0; i < 12; i++)
                    receipt.Observe(i, PageArgs("/x"), PageResult(new() { ["error"] = new JsonObject
                    { ["message"] = message + " " + new string('z', 900), ["code"] = "Synthetic" } }), true);
                return receipt.Receipt();
            }
            string[] outputs = await Task.WhenAll(Capture("FIRST"), Capture("SECOND"));
            Must(outputs[0].Contains("FIRST") && !outputs[0].Contains("SECOND") && !outputs[1].Contains("FIRST"));
            Must(outputs.All(s => s.Length < 5500 && s.Contains("excerptLimitReached=true")));
            DirectPrivateErrorReceipt local = new();
            local.Failure(2, new HttpRequestException("TRANSPORT-SECRET", null, HttpStatusCode.BadGateway));
            Must(local.Receipt().Contains("thrown transport exception") && local.Receipt().Contains("502") &&
                !local.Receipt().Contains("TRANSPORT-SECRET") && !local.Receipt().Contains(ReturnedReason));
        });
        await check("Installed native transport exception has no invented returned provider body", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.OnFetch = () => throw new HttpRequestException("TRANSPORT-SECRET", null, HttpStatusCode.BadGateway);
            ModelHandler model = new(Completion(tool: "fetch", args: Fetch("/me/drive"), id: "one"));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Read metadata", "personal"), default))!;
            string block = PrivateErrorPart(output);
            Must(block.Contains("thrown transport exception") && block.Contains("unavailable") &&
                !block.Contains("TRANSPORT-SECRET") && h.Handler.ToolCalls == 1 && model.Requests.Count == 1, output);
        });
        foreach (string audience in new[] { "personal", "channel", "changed" })
            await check("Private returned-error only terminal private SDK update not cards logs " + audience, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                var activity = ProgressActivity("Read channel data privately", audience == "channel" ? "channel" : "personal");
                using UiFixture ui = new(h, activity);
                h.Handler.SectionTwoResource = _ => ReturnedError();
                ModelHandler model = new(Completion(tool: "fetch", args: Fetch("/me/drive"), id: "one"), Completion("Partial"));
                await ui.Progress.RunAsync(async () =>
                {
                    string result = (await App(h, model, DirectOptions()).Handle(activity, default, ui.Progress))!;
                    if (audience == "changed") activity.Conversation!.IsGroup = true;
                    return result;
                });
                foreach (var row in ui.Wire.Requests)
                {
                    bool detail = row.Body.GetRawText().Contains(ReturnedReason);
                    Must(!detail || audience == "personal" && row.Method == HttpMethod.Put && !row.Body.TryGetProperty("attachments", out _));
                }
                Must(ui.Wire.Requests.Last().Body.GetRawText().Contains(ReturnedReason) == (audience == "personal"));
                Must(ui.Log.Lines.All(l => !l.Contains(ReturnedReason)) && h.Handler.ToolCalls == 1);
            });
        await check("PrivateNative returned error saved and delivered only matching personal job", async () =>
        {
            ModelHandler model = new(Completion(tool: "fetch", args: Fetch("/me/drive"), id: "one"), Completion("Partial"));
            await using PrivateHarness p = new(model);
            p.H.Handler.SectionTwoResource = _ => ReturnedError();
            PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
            await p.Worker.RunOne(default);
            Must(p.Store.Get(job.Id).Result!.Contains(ReturnedReason) && p.H.Handler.ToolCalls == 1);
            foreach (var row in p.Ui.Wire.Requests)
                if (row.Body.GetRawText().Contains(ReturnedReason))
                    Must(row.Body.GetProperty("conversation").GetProperty("id").GetString() == p.Personal.Conversation!.Id &&
                        row.Method == HttpMethod.Put && !row.Body.TryGetProperty("attachments", out _));
            Must(p.Ui.Wire.Requests.Last().Body.GetRawText().Contains(ReturnedReason));
            Must(!await p.Worker.RunOne(default) && p.H.Handler.ToolCalls == 1);
        });
    }
}
