using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static string NativeBudgetSchema(string shape)
    {
        if (shape == "unicode")
            return "type: string\ndescription: |\n  " + new string('\u754c', 400000);
        if (shape == "quotes")
            return "type: string\ndescription: |\n" +
                string.Concat(Enumerable.Repeat("  A quoted \"label\" and \\ slash <value> & label.\n", 8000));
        JsonObject properties = new();
        for (int i = 0; i < 5000; i++)
            properties["field" + i.ToString("D4")] = new JsonObject
            {
                ["type"] = "string", ["description"] = "Display label for this field."
            };
        return new JsonObject { ["type"] = "object", ["properties"] = properties }.ToJsonString();
    }

    private static async Task DirectBudgetChecks(Func<string, Func<Task>, Task> check)
    {
        await check("Direct coordinated Generous budgets preserve guarded and Standard defaults", () =>
        {
            NaturalLanguageOptions options = DirectOptions();
            NaturalLanguageBudgets direct = options.ResolveBudgets();
            Must(direct.ToolResultChars == 512000 && direct.ConversationChars == 1000000 &&
                direct.McpResponseBytes == 4194304 && direct.ModelRequestBytes == 8388608 && direct.ModelTurns == 32);
            Must(direct with
            {
                ModelTurns = 16, ToolResultChars = 32000, ConversationChars = 256000,
                McpResponseBytes = 1048576, ModelRequestBytes = 2097152
            } == NaturalLanguageBudgets.Generous);
            options.DirectMcp.Enabled = false;
            Must(options.ResolveBudgets() == NaturalLanguageBudgets.Generous);
            options.DirectMcp.Enabled = true;
            options.Budgets.Profile = "Standard";
            Must(options.ResolveBudgets() == NaturalLanguageBudgets.Standard);
            return Task.CompletedTask;
        });
        foreach (var bound in new[]
        {
            (Name: "ToolResultChars", Max: 1000000, GuardedMax: 128000),
            (Name: "ConversationChars", Max: 2000000, GuardedMax: 1000000),
            (Name: "McpResponseBytes", Max: 8388608, GuardedMax: 4194304),
            (Name: "ModelRequestBytes", Max: 16777216, GuardedMax: 4194304)
        })
            await check("Direct revised config bound exact and above " + bound.Name, async () =>
            {
                NaturalLanguageOptions options = DirectOptions();
                var property = typeof(NaturalLanguageBudgetOptions).GetProperty(bound.Name)!;
                property.SetValue(options.Budgets, bound.Max);
                options.Validate();
                property.SetValue(options.Budgets, bound.Max + 1);
                await Denied(() => { options.Validate(); return Task.CompletedTask; });
                options.DirectMcp.Enabled = false;
                property.SetValue(options.Budgets, bound.GuardedMax);
                options.Validate();
                property.SetValue(options.Budgets, bound.GuardedMax + 1);
                await Denied(() => { options.Validate(); return Task.CompletedTask; });
            });
        foreach (string shape in new[] { "json", "stringified-json", "quotes", "unicode" })
            await check("Direct complete large native schema reaches SDK fetch and answer " + shape, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                string schema = NativeBudgetSchema(shape);
                string catalogAnnotation = string.Concat(Enumerable.Repeat("Native attribute. ", 6000));
                h.Handler.CatalogTools![8]!["inputSchema"]!["properties"]!["jsonBody"]!["description"] = catalogAnnotation;
                Must(schema.Length >= 334689 && schema.Length < 500000, $"schema characters={schema.Length}");
                h.Handler.DiscoveryMcpResult = TextEnvelope(shape == "stringified-json" ? JsonSerializer.Serialize(schema) : schema);
                // Two Unicode schemas exercise >2MiB serialized model requests and >1MiB MCP transport.
                int schemaCalls = shape == "unicode" ? 2 : 1;
                JsonObject[] completions = Enumerable.Range(0, schemaCalls)
                    .Select(i => Completion(tool: "get_schema", args: new { path = Team, operationType = "fetch", format = "jsonschema" },
                        id: "schema-" + i))
                    .Concat([Completion(tool: "fetch", args: Fetch(Team), id: "fetch"), Completion("Native schema and fetch returned.")]).ToArray();
                ModelHandler model = new(completions);
                NaturalLanguageOptions options = DirectOptions();
                if (shape == "stringified-json") options.Budgets.ToolResultChars = 700000;
                string output = (await App(h, model, options).Handle(Harness.Activity("Inspect the schema then fetch"), default))!;
                Must(output.Contains("Native schema and fetch returned.") && h.Handler.ToolCalls == schemaCalls + 1 &&
                    h.Handler.Mutations == 0 && model.Requests.Count == schemaCalls + 2, output);
                foreach (JsonElement request in model.Requests.Skip(1))
                {
                    Must(request.GetProperty("tools").EnumerateArray()
                        .Single(t => t.GetProperty("function").GetProperty("name").GetString() == "custom_operation")
                        .GetProperty("function").GetProperty("parameters").GetProperty("properties")
                        .GetProperty("jsonBody").GetProperty("description").GetString() == catalogAnnotation);
                    JsonElement[] results = request.GetProperty("messages").EnumerateArray()
                        .Where(m => m.GetProperty("role").GetString() == "tool" &&
                            m.GetProperty("tool_call_id").GetString()!.StartsWith("schema-")).ToArray();
                    Must(results.Length >= 1);
                    foreach (JsonElement message in results)
                    {
                        string text = message.GetProperty("content").GetString()!;
                        Must(text.Length > 334689 && text.Length <= options.ResolveBudgets().ToolResultChars);
                        string complete = text.Split('\n', 2)[1];
                        Must(complete == (shape == "stringified-json" ? JsonSerializer.Serialize(schema) : schema),
                            "Native schema text spelling changed or was truncated in the actual SDK request.");
                    }
                    Must(Encoding.UTF8.GetByteCount(request.GetRawText()) <= options.ResolveBudgets().ModelRequestBytes);
                }
                if (shape == "unicode")
                    Must(h.Handler.DiscoveryEnvelopeBytes > 1048576 &&
                        Encoding.UTF8.GetByteCount(model.Requests[2].GetRawText()) > 2097152,
                        "Fixture must cross both previous byte defaults.");
                if (shape is "unicode" or "quotes")
                    Must(h.Handler.DiscoveryEnvelopeBytes > schema.Length &&
                        Encoding.UTF8.GetByteCount(model.Requests[1].GetRawText()) > schema.Length);
                Must(h.Handler.DiscoveryEnvelopeBytes <= options.ResolveBudgets().McpResponseBytes);
            });
        foreach (string name in new[] { "ToolResultChars", "ConversationChars", "McpResponseBytes", "ModelRequestBytes" })
            await check("Direct explicit smaller budget remains effective end to end " + name, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                h.Handler.DiscoveryMcpResult = TextEnvelope(NativeBudgetSchema("json"));
                NaturalLanguageOptions options = DirectOptions();
                typeof(NaturalLanguageBudgetOptions).GetProperty(name)!.SetValue(options.Budgets, name == "ToolResultChars" ? 128000 : 262144);
                ModelHandler model = new(Completion(tool: "get_schema",
                    args: new { path = Team, operationType = "fetch", format = "jsonschema" }));
                string output = (await App(h, model, options).Handle(Harness.Activity("Inspect"), default))!;
                Must(!output.Contains("Direct MCP AI answer") && h.Handler.ToolCalls == 1 && model.Requests.Count == 1, output);
                Must(output.Contains(name) || name == "McpResponseBytes" && output.Contains("response"), output);
            });
        foreach (int limit in new[] { 512000, 1000000 })
            await check("Direct tool-result exact boundary preserves all text then rejects excess " + limit, async () =>
            {
                NaturalLanguageOptions options = DirectOptions();
                options.Budgets.ToolResultChars = limit;
                NaturalLanguageBudgets budgets = options.ResolveBudgets();
                ToolReply Reply(string text) => new(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = text }]
                }, new("fixture", "reused", 0, 0, 0, 0, 0, 0));
                int header = DirectMcpPresentation.Result(Reply("x"), budgets).Length - 1;
                string exact = string.Concat(Enumerable.Repeat("a ", (budgets.ToolResultChars - header) / 2));
                exact = exact.PadRight(budgets.ToolResultChars - header, 'a');
                Must(DirectMcpPresentation.Result(Reply(exact), budgets).Length == budgets.ToolResultChars);
                await Denied(() => { DirectMcpPresentation.Result(Reply(exact + "x"), budgets); return Task.CompletedTask; });
            });
        await check("Direct large supplied text is unchanged including credential-shaped text", () =>
        {
            string prefix = string.Concat(Enumerable.Repeat("Safe label. ", 35000));
            string output = DirectMcpPresentation.Text(prefix + "\naccess_token: PRIVATE-END\nSafe ending.", 512000);
            Must(output == prefix + "\naccess_token: PRIVATE-END\nSafe ending.");
            return Task.CompletedTask;
        });
    }
}
