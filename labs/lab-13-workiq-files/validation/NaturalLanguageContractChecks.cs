using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static JsonArray ToolsWithOverrides()
    {
        JsonArray tools = Tools();
        foreach (JsonNode? tool in tools)
        {
            JsonObject schema = tool!["inputSchema"]!.AsObject();
            JsonObject properties = schema["properties"]!.AsObject();
            properties["agentId"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["default"] = null };
            properties["headers"] = new JsonObject { ["type"] = new JsonArray("object", "null"), ["additionalProperties"] = true };
            if (tool["name"]!.GetValue<string>() == "fetch_blob")
                properties["format"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["default"] = null };
        }
        return tools;
    }

}
