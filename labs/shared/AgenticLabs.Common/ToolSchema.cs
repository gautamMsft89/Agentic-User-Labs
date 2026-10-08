using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkIqFiles;

// Conservative JSON Schema subset. Unknown validation keywords fail closed, never silently ignored.
internal static class ToolSchema
{
    private static readonly HashSet<string> Keywords =
        ["type", "properties", "required", "additionalProperties", "items", "enum", "anyOf", "oneOf",
         "description", "title", "default", "$schema", "minLength", "maxLength", "minItems", "maxItems"];
    internal static void Check(JsonElement schema, int depth = 0)
    {
        if (depth > 12 || schema.ValueKind != JsonValueKind.Object || schema.GetRawText().Length > 16384)
            throw new LabException("Unsupported/oversized MCP input schema.");
        if (schema.EnumerateObject().Select(p => p.Name).Distinct().Count() != schema.EnumerateObject().Count())
            throw new LabException("Duplicate schema keywords.");
        foreach (JsonProperty p in schema.EnumerateObject())
        {
            if (p.Name is "$ref" or "$dynamicRef" or "$recursiveRef")
                throw new LabException("MCP input schema references are unavailable; no reference resolution or dispatch.");
            if (!Keywords.Contains(p.Name)) throw new LabException("Unsupported MCP schema keyword; tool not exposed.");
            if (p.Name == "properties")
            {
                if (p.Value.ValueKind != JsonValueKind.Object) throw new LabException("Malformed schema properties.");
                if (p.Value.EnumerateObject().Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != p.Value.EnumerateObject().Count())
                    throw new LabException("Duplicate schema property names.");
                foreach (JsonProperty child in p.Value.EnumerateObject()) Check(child.Value, depth + 1);
            }
            if (p.Name == "items") Check(p.Value, depth + 1);
            if (p.Name is "anyOf" or "oneOf")
            {
                if (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() is < 1 or > 8)
                    throw new LabException("Malformed schema alternatives.");
                foreach (JsonElement child in p.Value.EnumerateArray()) Check(child, depth + 1);
            }
            if (p.Name == "additionalProperties")
            {
                if (p.Value.ValueKind == JsonValueKind.Object) Check(p.Value, depth + 1);
                else if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new LabException($"Malformed schema additionalProperties; kind [{p.Value.ValueKind}], schemaDepth [{depth}]; expected boolean or schema object.");
            }
            if (p.Name == "type")
            {
                JsonElement[] types = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().ToArray() : [p.Value];
                if (types.Length == 0 || types.Any(t => t.ValueKind != JsonValueKind.String ||
                    t.GetString() is not ("object" or "array" or "string" or "null" or "boolean" or "integer" or "number")))
                    throw new LabException("Unsupported schema type.");
            }
            if (p.Name == "required" && (p.Value.ValueKind != JsonValueKind.Array ||
                p.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)))
                throw new LabException("Malformed required schema.");
            if (p.Name == "enum" && (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() == 0))
                throw new LabException("Malformed enum schema.");
            if (p.Name is "minLength" or "maxLength" or "minItems" or "maxItems" &&
                (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out int bound) || bound < 0))
                throw new LabException("Malformed schema bound.");
        }
    }
    internal static void Validate(JsonElement schema, JsonElement value)
    {
        Check(schema);
        if (value.ValueKind != JsonValueKind.Object || !Matches(schema, value))
            throw new LabException("Model arguments do not match the discovered MCP input schema; no dispatch.");
    }
    private static bool Matches(JsonElement s, JsonElement v)
    {
        if (s.TryGetProperty("type", out JsonElement type))
        {
            bool Is(string? t) => t switch
            {
                "object" => v.ValueKind == JsonValueKind.Object, "array" => v.ValueKind == JsonValueKind.Array,
                "string" => v.ValueKind == JsonValueKind.String, "null" => v.ValueKind == JsonValueKind.Null,
                "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "number" => v.ValueKind == JsonValueKind.Number,
                "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _), _ => false
            };
            if (!(type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Any(t => Is(t.GetString())) : Is(type.GetString()))) return false;
        }
        if (s.TryGetProperty("enum", out JsonElement choices) && !choices.EnumerateArray().Any(c => JsonElement.DeepEquals(c, v))) return false;
        foreach (string kind in new[] { "anyOf", "oneOf" })
            if (s.TryGetProperty(kind, out JsonElement branches))
            {
                int matches = branches.EnumerateArray().Count(b => Matches(b, v));
                if (matches == 0 || kind == "oneOf" && matches != 1) return false;
            }
        if (v.ValueKind == JsonValueKind.Object)
        {
            JsonProperty[] props = v.EnumerateObject().ToArray();
            if (props.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != props.Length) return false;
            if (s.TryGetProperty("required", out JsonElement required) && required.EnumerateArray().Any(r => !v.TryGetProperty(r.GetString()!, out _))) return false;
            bool hasProps = s.TryGetProperty("properties", out JsonElement properties);
            foreach (JsonProperty p in props)
                if (hasProps && properties.TryGetProperty(p.Name, out JsonElement child))
                { if (!Matches(child, p.Value)) return false; }
                else if (s.TryGetProperty("additionalProperties", out JsonElement additional))
                {
                    if (additional.ValueKind == JsonValueKind.False ||
                        additional.ValueKind == JsonValueKind.Object && !Matches(additional, p.Value)) return false;
                }
        }
        if (v.ValueKind == JsonValueKind.Array)
        {
            if (!Bounds(s, v.GetArrayLength(), "minItems", "maxItems")) return false;
            if (s.TryGetProperty("items", out JsonElement items) && v.EnumerateArray().Any(i => !Matches(items, i))) return false;
        }
        return v.ValueKind != JsonValueKind.String || Bounds(s, v.GetString()!.Length, "minLength", "maxLength");
    }
    private static bool Bounds(JsonElement s, int size, string min, string max) =>
        (!s.TryGetProperty(min, out JsonElement a) || size >= a.GetInt32()) &&
        (!s.TryGetProperty(max, out JsonElement b) || size <= b.GetInt32());

    internal static JsonElement ForModel(JsonElement schema)
    {
        Check(schema);
        JsonNode clean = JsonNode.Parse(schema.GetRawText())!;
        void Strip(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                obj.Remove("description"); obj.Remove("title"); obj.Remove("default"); obj.Remove("$schema");
                if (obj["properties"] is JsonObject props)
                    foreach (var p in props) Strip(p.Value);
                Strip(obj["items"]);
                Strip(obj["additionalProperties"]);
                Strip(obj["anyOf"]); Strip(obj["oneOf"]);
            }
            else if (node is JsonArray array) foreach (JsonNode? child in array) Strip(child);
        }
        Strip(clean);
        return JsonSerializer.SerializeToElement(clean);
    }
}
