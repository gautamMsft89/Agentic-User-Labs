using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static async Task MapSchemaChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (string additional in new[] { "true", "false", "{}", """{"type":"string","maxLength":4}""" })
            await check("MCP additionalProperties native semantics " + additional, async () =>
            {
                using JsonDocument schema = JsonDocument.Parse(
                    """{"type":"object","properties":{"known":{"type":"integer"}},"additionalProperties":""" + additional + "}");
                ToolSchema.Check(schema.RootElement);
                ToolSchema.Validate(schema.RootElement, Json(new { known = 1 }));
                if (additional == "false")
                    await Denied(() => { ToolSchema.Validate(schema.RootElement, Json(new { extra = "blue" })); return Task.CompletedTask; });
                else
                    ToolSchema.Validate(schema.RootElement, Json(new { extra = "blue" }));
                if (additional.Contains("string"))
                {
                    await Denied(() => { ToolSchema.Validate(schema.RootElement, Json(new { extra = 5 })); return Task.CompletedTask; });
                    await Denied(() => { ToolSchema.Validate(schema.RootElement, Json(new { extra = "long-value" })); return Task.CompletedTask; });
                }
                else if (additional != "false") ToolSchema.Validate(schema.RootElement, Json(new { extra = new { any = 5 } }));
                Must(JsonElement.DeepEquals(ToolSchema.ForModel(schema.RootElement).GetProperty("additionalProperties"),
                    schema.RootElement.GetProperty("additionalProperties")));
            });
        await check("MCP map values preserve nested arrays nullable enum oneOf and required constraints", async () =>
        {
            using JsonDocument schema = JsonDocument.Parse("""
                {"type":"object","additionalProperties":{"type":"object","properties":{
                  "values":{"type":"array","minItems":1,"maxItems":2,"items":{"oneOf":[{"type":"null"},{"type":"string","enum":["red","blue"]}]}}
                },"required":["values"],"additionalProperties":false}}
                """);
            ToolSchema.Validate(schema.RootElement, Json(new { first = new { values = new string?[] { "red", null } } }));
            foreach (object invalid in new object[]
            {
                new { first = new { } }, new { first = new { values = Array.Empty<string>() } },
                new { first = new { values = new[] { "green" } } },
                new { first = new { values = new[] { "red" }, unknown = true } }
            })
                await Denied(() => { ToolSchema.Validate(schema.RootElement, Json(invalid)); return Task.CompletedTask; });
        });
        await check("MCP schema-map model preparation strips annotations recursively but keeps constraints", () =>
        {
            using JsonDocument schema = JsonDocument.Parse("""
                {"type":"object","additionalProperties":{"type":"object","description":"PRIVATE-map",
                "properties":{"name":{"type":"string","description":"PRIVATE-name","maxLength":5}},
                "required":["name"],"additionalProperties":{"type":"array","default":[],"items":{"type":"string","title":"PRIVATE-item","enum":["safe"]}}}}
                """);
            JsonElement model = ToolSchema.ForModel(schema.RootElement);
            Must(!model.GetRawText().Contains("PRIVATE") && !model.GetRawText().Contains("default") &&
                model.GetProperty("additionalProperties").GetProperty("properties").GetProperty("name").GetProperty("maxLength").GetInt32() == 5 &&
                model.GetRawText().Contains("enum"));
            return Task.CompletedTask;
        });
        await check("Direct ordinary header-map catalog reaches model without exposing forbidden knobs", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            foreach (JsonNode? tool in h.Handler.CatalogTools!.Take(4))
                tool!["inputSchema"]!["properties"]!["headers"] = new JsonObject
                { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } };
            string original = h.Handler.CatalogTools!.ToJsonString();
            ModelHandler model = new(Completion(tool: "fetch", args: Fetch(Team)), Completion("Native read."));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Read"), default))!;
            Must(output.Contains("Native read.") && h.Handler.ToolCalls == 1 && h.Handler.Mutations == 0, output);
            foreach (JsonElement tool in model.Requests[0].GetProperty("tools").EnumerateArray())
                Must(!tool.GetProperty("function").GetProperty("parameters").GetProperty("properties").TryGetProperty("headers", out _));
            Must(original == h.Handler.CatalogTools.ToJsonString());
        });
        foreach (string shape in new[] { "nested-map", "root-map", "root-map-no-properties", "open-root" })
            await check("Direct native dictionary preserved through immediate SDK call and exact wire " + shape, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                JsonObject schema = h.Handler.CatalogTools![8]!["inputSchema"]!.AsObject();
                JsonObject mapValue = new() { ["type"] = "string", ["enum"] = new JsonArray("blue", "{}") };
                object args;
                if (shape == "nested-map")
                {
                    schema["properties"]!["labels"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = mapValue };
                    args = new { jsonBody = "{}", labels = new { chosen = "blue" } };
                }
                else
                {
                    schema["additionalProperties"] = shape == "open-root" ? JsonValue.Create(true) : mapValue;
                    if (shape == "root-map-no-properties") { schema.Remove("properties"); schema.Remove("required"); }
                    args = new { jsonBody = "{}", chosen = "blue" };
                }
                string original = schema.ToJsonString();
                ModelHandler model = new(Completion(tool: "custom_operation", args: args), Completion("Native map accepted."));
                WorkIqIngress app = App(h, model, DirectOptions());
                string output = (await app.Handle(Harness.Activity("Use dictionary operation"), default))!;
                JsonElement modelSchema = model.Requests[0].GetProperty("tools").EnumerateArray()
                    .Single(t => t.GetProperty("function").GetProperty("name").GetString() == "custom_operation")
                    .GetProperty("function").GetProperty("parameters");
                JsonElement actualMap = shape == "nested-map" ? modelSchema.GetProperty("properties").GetProperty("labels").GetProperty("additionalProperties") :
                    modelSchema.GetProperty("additionalProperties");
                Must(actualMap.ValueKind == (shape == "open-root" ? JsonValueKind.True : JsonValueKind.Object));
                Must(output.Contains("Native map accepted.") && h.Handler.Mutations == 1 &&
                    JsonElement.DeepEquals(h.Handler.Calls.Single().Args, Json(args)) && original == schema.ToJsonString(), output);
            });
        foreach (string key in new[] { "agentId", "agent_id", "headers", "authorization", "endpoint", "callbackUrl", "token" })
            await check("Direct open/schema-valued maps never permit reserved key " + key, async () =>
            {
                foreach (bool nested in new[] { false, true })
                {
                    await using Harness h = new(); DirectEnable(h);
                    JsonObject schema = h.Handler.CatalogTools![8]!["inputSchema"]!.AsObject();
                    schema["additionalProperties"] = new JsonObject();
                    JsonObject args = new() { ["jsonBody"] = nested ? JsonSerializer.Serialize(new Dictionary<string, object?> { [key] = null }) : "{}" };
                    if (!nested) args[key] = null;
                    ModelHandler model = new(Completion(tool: "custom_operation", args: args));
                    string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Operation"), default))!;
                    Must(output.Contains("override denied") && !output.Contains("/mcp-confirm ") && h.Handler.ToolCalls == 0, output);
                }
            });
        await check("Direct native argument schema rejection belongs to server on immediate dispatch", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.CatalogTools![8]!["inputSchema"]!["additionalProperties"] = new JsonObject { ["type"] = "string" };
            ModelHandler model = new(Completion(tool: "custom_operation", args: new { jsonBody = "{}", extra = 123 }));
            h.Handler.MutationResourceStatus = 400;
            WorkIqIngress app = App(h, model, DirectOptions());
            string output = (await app.Handle(Harness.Activity("Operation"), default))!;
            Must(output.Contains("returned an error") && h.Handler.ToolCalls == 1 &&
                h.Handler.Calls.Single().Args.GetProperty("extra").GetInt32() == 123, output);
        });
        foreach (string invalid in new[] { "null", "[]", "\"false\"", "7", """{"type":"PRIVATE-invalid"}""" })
            await check("Direct native schema passes unchanged for provider compatibility decision " + invalid, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                h.Handler.CatalogTools![8]!["inputSchema"]!["additionalProperties"] = JsonNode.Parse(invalid);
                ModelHandler model = new(new JsonObject { ["error"] = new JsonObject
                { ["code"] = "invalid_function_parameters", ["param"] = "tools[8].function.parameters", ["message"] = "PRIVATE native schema rejected" } })
                    { Status = System.Net.HttpStatusCode.BadRequest };
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Read"), default))!;
                Must(output.Contains("HTTP 400") && !output.Contains("PRIVATE") &&
                    model.Requests.Count == 1 && h.Handler.ToolCalls == 0, output);
                JsonElement sent = model.Requests[0].GetProperty("tools").EnumerateArray()
                    .Single(t => t.GetProperty("function").GetProperty("name").GetString() == "custom_operation")
                    .GetProperty("function").GetProperty("parameters").GetProperty("additionalProperties");
                using JsonDocument expected = JsonDocument.Parse(invalid);
                Must(JsonElement.DeepEquals(sent, expected.RootElement));
            });
        foreach (string unsupported in new[] { "pattern", "root-composition" })
            await check("Direct native schema feature passes through without subset validator " + unsupported, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                JsonObject schema = h.Handler.CatalogTools![8]!["inputSchema"]!.AsObject();
                if (unsupported == "pattern") schema["properties"]!["jsonBody"]!["pattern"] = "^PRIVATE-pattern$";
                else schema["anyOf"] = new JsonArray(new JsonObject());
                ModelHandler model = new(Completion(tool: "fetch", args: Fetch(Team)), Completion("Read accepted."));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Read"), default))!;
                Must(output.Contains("Read accepted.") && !output.Contains("PRIVATE"), output);
                JsonElement function = model.Requests[0].GetProperty("tools").EnumerateArray()
                    .Single(t => t.GetProperty("function").GetProperty("name").GetString() == "custom_operation").GetProperty("function");
                Must(JsonElement.DeepEquals(function.GetProperty("parameters"), Json(schema)) &&
                    !function.TryGetProperty("strict", out _));
            });
        await check("Direct excluded opaque delegate cannot be selected despite native schema pass through", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            ModelHandler model = new(Completion(tool: "ask", args: new { query = "Read" }));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Operation"), default))!;
            Must(output.Contains("unavailable direct tool") && h.Handler.ToolCalls == 0, output);
        });
        foreach (string failure in new[] { "map-ref", "required-override", "non-object-schema" })
            await check("Direct schema security and protocol guards remain fatal " + failure, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                JsonObject schema = h.Handler.CatalogTools![8]!["inputSchema"]!.AsObject();
                if (failure == "map-ref") schema["additionalProperties"] = new JsonObject { ["$ref"] = "https://PRIVATE.invalid/schema" };
                if (failure == "required-override") schema["required"]!.AsArray().Add("agentId");
                if (failure == "non-object-schema") h.Handler.CatalogTools[8]!["inputSchema"] = new JsonArray();
                ModelHandler model = new();
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Read"), default))!;
                Must((failure == "non-object-schema" ? output.Contains("at MCP catalog") : output.Contains("native schema rejected")) &&
                    !output.Contains("PRIVATE") && model.Requests.Count == 0 && h.Handler.ToolCalls == 0, output);
            });
        await check("Direct SDK forwards local definitions references annotations and native constraints non-strict", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            JsonObject schema = h.Handler.CatalogTools![8]!["inputSchema"]!.AsObject();
            schema["$defs"] = new JsonObject { ["label"] = new JsonObject
                { ["type"] = new JsonArray("string", "null"), ["pattern"] = "^[a-z]+$", ["description"] = "Native label documentation." } };
            schema["properties"]!["labels"] = new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = new JsonObject { ["$ref"] = "#/$defs/label" },
                ["examples"] = new JsonArray(new JsonObject { ["name"] = "blue" })
            };
            schema["allOf"] = new JsonArray(new JsonObject { ["required"] = new JsonArray("jsonBody") });
            object args = new { jsonBody = "{}", labels = new { name = "blue" } };
            ModelHandler model = new(Completion(tool: "custom_operation", args: args), Completion("Native result."));
            WorkIqIngress app = App(h, model, DirectOptions());
            string result = (await app.Handle(Harness.Activity("Native operation"), default))!;
            JsonElement function = model.Requests[0].GetProperty("tools").EnumerateArray()
                .Single(t => t.GetProperty("function").GetProperty("name").GetString() == "custom_operation").GetProperty("function");
            Must(!function.TryGetProperty("strict", out _) && JsonElement.DeepEquals(function.GetProperty("parameters"), Json(schema)), function.GetRawText());
            Must(result.Contains("Native result.") && h.Handler.Mutations == 1 && JsonElement.DeepEquals(h.Handler.Calls.Single().Args, Json(args)), result);
        });
    }
}
