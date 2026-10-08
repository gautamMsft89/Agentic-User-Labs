using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static JsonObject RawArgumentCompletion(string tool, string raw, string id = "raw-arguments")
    {
        JsonObject completion = Completion(tool: tool, args: new { }, id: id);
        completion["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"] = raw;
        return completion;
    }

    private static async Task DirectDuplicateChecks(Func<string, Func<Task>, Task> check)
    {
        const string path = "/me/drive?$select=id,driveType,owner";
        foreach (bool identical in new[] { true, false })
            await check("Private native SDK raw duplicate fetch reproduces hidden normalized key " + identical, async () =>
            {
                string raw = """{"entityUrls":["PRIVATE-earlier-value"],"entityUrls":["/me/drive?$select=id,driveType,owner"]}""";
                if (identical) raw = raw.Replace("PRIVATE-earlier-value", path, StringComparison.Ordinal);
                await using PrivateHarness p = new(new(
                    Completion(tool: "search_paths", args: new { filter = "drive|file" }, id: "discovery"),
                    RawArgumentCompletion("fetch", raw)));
                PrivateJob job = await p.Initiate();
                var response = await InvokeWire(p, PrivateInvoke(p, job));
                Must(response.Body.GetProperty("statusCode").GetInt32() == 200);
                await p.Authenticate(response.Body.GetProperty("value").GetProperty("actions")[0].GetProperty("url").GetString()!);
                await p.Worker.RunOne(default);
                string output = p.Store.Get(job.Id).Result!;
                Must(p.Model.Requests[0].GetProperty("messages")[0].GetProperty("content").GetString()!.Contains(DirectMcpContract.ArgumentGuidance));
                Must(output.Contains("tool 2 fetch validation") &&
                    output.Contains("duplicateKey=entityUrls; keyClass=NativeArgument; occurrences=2; objectDepth=0; source=ModelArguments") &&
                    output.Contains("rawValuesIdentical=" + identical) && output.Contains("unvalidated or prohibited") &&
                    !output.Contains(path) && !output.Contains("PRIVATE-earlier-value") &&
                    p.H.Handler.ToolCalls == 1 && p.H.Handler.Calls.Single().Tool == "search_paths" &&
                    p.H.Handler.Mutations == 0 && p.Model.Requests.Count == 2 && !await p.Worker.RunOne(default), output);
                using JsonDocument parsed = JsonDocument.Parse(raw);
                Must(parsed.RootElement.EnumerateObject().Count() == 2);
                JsonObject formerDisplay = new();
                foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
                    formerDisplay[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                Must(formerDisplay.Count == 1 && formerDisplay["entityUrls"]![0]!.GetValue<string>() == path);
            });
        await check("Private native SDK exact single fetch key dispatches without duplicate fabrication", async () =>
        {
            string raw = """{"entityUrls":["/me/drive?$select=id,driveType,owner"]}""";
            await using PrivateHarness p = new(new(RawArgumentCompletion("fetch", raw), Completion("Synthetic drive returned.")));
            p.H.Handler.SectionTwoResults[path] = new() { ["id"] = "synthetic-drive" };
            PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
            await p.Worker.RunOne(default);
            Must(p.H.Handler.ToolCalls == 1 && p.H.Handler.Calls.Single().Args.GetRawText() == raw &&
                p.Store.Get(job.Id).Result!.Contains("Synthetic drive returned."));
        });
        var duplicates = new[]
        {
            (Raw: """{"entityUrls":[],"\u0065ntityUrls":[]}""", Key: "entityUrls", Class: "NativeArgument", Depth: 0, Source: "ModelArguments", Count: 2, Same: true),
            (Raw: """{"nested":{"path":"PRIVATE-a","path":"PRIVATE-b","path":"PRIVATE-c"}}""", Key: "path", Class: "NativeArgument", Depth: 1, Source: "ModelArguments", Count: 3, Same: false),
            (Raw: """{"records":[{"PRIVATE-field":"PRIVATE-a","PRIVATE-field":"PRIVATE-a"}]}""", Key: "withheld", Class: "Withheld", Depth: 2, Source: "ModelArguments", Count: 2, Same: true),
            (Raw: """{"jsonBody":"{\"name\":\"PRIVATE-a\",\"name\":\"PRIVATE-b\"}"}""", Key: "withheld", Class: "Withheld", Depth: 1, Source: "JsonBody", Count: 2, Same: false),
            (Raw: """{"jsonBody":"{\"members\":[{\"userId\":\"PRIVATE-a\",\"userId\":\"PRIVATE-a\"}]}"}""", Key: "withheld", Class: "Withheld", Depth: 3, Source: "JsonBody", Count: 2, Same: true),
            (Raw: """{"jsonBody":"{\"jsonBody\":\"{\\\"path\\\":1,\\\"path\\\":2}\"}"}""", Key: "path", Class: "NativeArgument", Depth: 2, Source: "JsonBody", Count: 2, Same: false),
            (Raw: """{"authorization":"PRIVATE-token","authorization":"PRIVATE-token"}""", Key: "authorization", Class: "ReservedOverride", Depth: 0, Source: "ModelArguments", Count: 2, Same: true),
            (Raw: """{"jsonBody":"{\"ACCESStoken\":\"PRIVATE-token\",\"ACCESStoken\":\"PRIVATE-other\"}"}""", Key: "accessToken", Class: "ReservedOverride", Depth: 1, Source: "JsonBody", Count: 2, Same: false),
            (Raw: """{"jsonBody":"{\"headers\":{},\"headers\":{}}"}""", Key: "headers", Class: "ReservedOverride", Depth: 1, Source: "JsonBody", Count: 2, Same: true),
            (Raw: """{"path":{"a":1},"path":{ "a":1}}""", Key: "path", Class: "NativeArgument", Depth: 0, Source: "ModelArguments", Count: 2, Same: false)
        };
        foreach (var fixture in duplicates)
            await check("Direct SDK duplicate evidence preserves rejection " + fixture.Source + " " + fixture.Key + " " + fixture.Depth + " " + fixture.Count, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                ModelHandler model = new(RawArgumentCompletion("custom_operation", fixture.Raw));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Synthetic raw argument validation"), default))!;
                Must(model.Requests[0].GetProperty("messages")[0].GetProperty("content").GetString()!.Contains(DirectMcpContract.ArgumentGuidance));
                Must(output.Contains($"duplicateKey={fixture.Key}; keyClass={fixture.Class}; occurrences={fixture.Count}; objectDepth={fixture.Depth}; source={fixture.Source}") &&
                    output.Contains("rawValuesIdentical=" + fixture.Same) &&
                    !output.Contains("PRIVATE-") && h.Handler.ToolCalls == 0 && h.Handler.Mutations == 0 &&
                    model.Requests.Count == 1, output);
            });
        foreach (string raw in new[]
        {
            """{"records":[{"name":"one"},{"name":"two"}],"left":{"id":1},"right":{"id":2},"jsonBody":"{}"}""",
            """{"jsonBody":"{\"members\":[{\"id\":1},{\"id\":2}],\"left\":{\"id\":1},\"right\":{\"id\":2}}"}""",
            """{"name":"one","Name":"two","jsonBody":"{}"}""",
            """{"entityUrls":["same","same"],"jsonBody":"{}"}"""
        })
            await check("Direct SDK accepts independent objects arrays and case-distinct business keys " + raw.Length, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                ModelHandler model = new(RawArgumentCompletion("custom_operation", raw), Completion("Native synthetic return."));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Synthetic payload"), default))!;
                using JsonDocument expected = JsonDocument.Parse(raw);
                Must(h.Handler.ToolCalls == 1 && h.Handler.Mutations == 1 &&
                    JsonElement.DeepEquals(h.Handler.Calls.Single().Args, expected.RootElement) &&
                    output.Contains("Native synthetic return."), output);
            });
        await check("Direct reserved case variants still reject even without exact-name duplicates", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            ModelHandler model = new(RawArgumentCompletion("custom_operation",
                """{"jsonBody":"{\"authorization\":\"PRIVATE-a\",\"Authorization\":\"PRIVATE-b\"}"}"""));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Synthetic payload"), default))!;
            Must(output.Contains("override denied") && !output.Contains("PRIVATE-") && h.Handler.ToolCalls == 0, output);
        });
    }
}
