using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace WorkIqFiles;

internal static class DirectMcpPresentation
{
    private static bool BinaryField(JsonProperty property) =>
        property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Array &&
        (property.Name.Equals("base64Content", StringComparison.OrdinalIgnoreCase) ||
         property.Name.Equals("base64", StringComparison.OrdinalIgnoreCase) ||
         property.Name.Equals("bytes", StringComparison.OrdinalIgnoreCase) ||
         property.Name.Equals("binary", StringComparison.OrdinalIgnoreCase));
    private static bool HasBinary(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(p => BinaryField(p) || HasBinary(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(HasBinary),
        _ => false
    };
    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
    internal static string Text(string text, int limit, string budget = "ToolResultChars") =>
        NaturalLanguageText.Bounded(text, limit, budget);
    internal static string UserInput(string text, int limit) => Text(text, limit, "InputChars");

    internal static string PathEvidence(JsonElement args)
    {
        if (!args.TryGetProperty("parentUrl", out JsonElement parent) || parent.ValueKind != JsonValueKind.String) return "";
        string path = Uri.UnescapeDataString(parent.GetString()!);
        bool marker = path.Contains("[opaque value redacted]", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("[redacted]", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("[long opaque/binary value withheld]", StringComparison.OrdinalIgnoreCase);
        return $"; outgoing path facts: pathContainsRedactionMarker={marker}; lexical evidence only";
    }

    internal static string Arguments(JsonElement args, int limit)
    {
        if (DirectMcpContract.FindDuplicate(args) is not null)
            return "[argument display omitted: duplicate fields; no normalized first/last-value display]";
        bool binary = HasBinary(args);
        if (args.TryGetProperty("jsonBody", out JsonElement body) && body.ValueKind == JsonValueKind.String)
        {
            using JsonDocument doc = JsonDocument.Parse(body.GetString()!, new JsonDocumentOptions { MaxDepth = 16 });
            binary |= HasBinary(doc.RootElement);
        }
        if (binary) return "[argument display omitted: binary payload; native dispatch unchanged]";
        const string heading = "[Selected native arguments; original jsonBody string retained; JSON-escaped inert text; not raw HTTP bytes; dispatch status follows] ";
        // Serialize for inert presentation only. Native dispatch uses the original JsonElement/string.
        string text = JsonSerializer.Serialize(args);
        return heading.Length + text.Length <= limit ? heading + text :
            "[argument display omitted: ArgumentTraceChars limit; not a complete capture; dispatch unchanged]";
    }

    internal static string Result(ToolReply reply, NaturalLanguageBudgets budgets)
    {
        List<string> additions = [];
        JsonNode? WithoutBinary(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                JsonObject result = new();
                foreach (JsonProperty p in value.EnumerateObject())
                    result.Add(p.Name, BinaryField(p) ? JsonValue.Create("[binary field omitted; not text ingestion]") : WithoutBinary(p.Value));
                return result;
            }
            if (value.ValueKind == JsonValueKind.Array) return new JsonArray(value.EnumerateArray().Select(WithoutBinary).ToArray());
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()!;
                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException) { }
                if (doc is not null)
                {
                    using (doc)
                        if (ContainsBinary(doc.RootElement, 0)) return JsonValue.Create(WithoutBinary(doc.RootElement)!.ToJsonString());
                }
            }
            return JsonNode.Parse(value.GetRawText());
        }
        bool ContainsBinary(JsonElement value, int depth)
        {
            if (depth > 32) throw new LabException("Direct MCP result depth exceeded; entire output omitted.");
            if (HasBinary(value)) return true;
            if (value.ValueKind == JsonValueKind.String)
            {
                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(value.GetString()!, new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException) { }
                if (doc is not null) { using (doc) return ContainsBinary(doc.RootElement, depth + 1); }
            }
            if (value.ValueKind == JsonValueKind.Object) return value.EnumerateObject().Any(p => ContainsBinary(p.Value, depth + 1));
            if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(v => ContainsBinary(v, depth + 1));
            return false;
        }
        void Inspect(JsonElement value, int depth)
        {
            if (depth > 32) throw new LabException("Direct MCP result JSON depth exceeded; entire output omitted.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
                    throw new LabException("Direct MCP result duplicate JSON fields; entire output omitted.");
                if (value.TryGetProperty("base64Content", out JsonElement encoded) && encoded.ValueKind == JsonValueKind.String &&
                    value.TryGetProperty("contentType", out JsonElement mime) && mime.ValueKind == JsonValueKind.String &&
                    mime.GetString()!.Split(';')[0].Trim().Equals("text/plain", StringComparison.OrdinalIgnoreCase))
                {
                    if (encoded.GetString()!.Length > 87384) throw new LabException("Direct MCP plain-text blob exceeds 64KiB encoding bound.");
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(encoded.GetString()!); }
                    catch (FormatException) { throw new LabException("Direct MCP blob has malformed base64; no text supplied."); }
                    if (bytes.Length > 65536) throw new LabException("Direct MCP plain-text blob exceeds 64KiB.");
                    string text;
                    try { text = new UTF8Encoding(false, true).GetString(bytes); }
                    catch (DecoderFallbackException) { throw new LabException("Direct MCP blob is not valid UTF8 plain text."); }
                    additions.Add(JsonSerializer.Serialize(new { decodedPlainText = text,
                        textQualification = "Service-declared text/plain; no separate filename/ancestry validation." }));
                }
                foreach (JsonProperty p in value.EnumerateObject()) Inspect(p.Value, depth + 1);
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in value.EnumerateArray()) Inspect(item, depth + 1);
            else if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()!;
                JsonDocument? doc;
                try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException) { return; } // Arbitrary native text need not be JSON; it is still passed verbatim.
                using (doc) Inspect(doc.RootElement, depth + 1);
            }
        }
        List<string> blocks = [];
        if (reply.Result.StructuredContent is JsonElement structured)
        {
            Inspect(structured, 0);
            blocks.Add(ContainsBinary(structured, 0) ? WithoutBinary(structured)!.ToJsonString() : structured.GetRawText());
        }
        foreach (ContentBlock block in reply.Result.Content)
        {
            if (block is TextContentBlock plain)
            {
                JsonElement content = JsonSerializer.SerializeToElement(plain.Text);
                Inspect(content, 0);
                blocks.Add(ContainsBinary(content, 0) ? WithoutBinary(content)!.GetValue<string>() : plain.Text);
            }
            else blocks.Add("[Non-text MCP block omitted from text; existing bounded image handling is separate; no URL transfer.]");
        }
        string output = $"MCP envelope isError={reply.Result.IsError == true}. Untrusted native content (no content scrubbing):\n" +
            string.Join("\n", blocks);
        if (additions.Count > 0) output += "\nAdditional existing bounded UTF8 decoding:\n" + string.Join("\n", additions);
        NaturalLanguageBudgets.Require("ToolResultChars", output.Length, budgets.ToolResultChars);
        return output;
    }

    internal static bool Error(ToolReply reply)
    {
        if (reply.Result.IsError == true) return true;
        bool Status(JsonElement value, int depth) => depth <= 32 && (value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Any(p =>
                p.Name.Equals("statusCode", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int code) && code >= 400 ||
                Status(p.Value, depth + 1)),
            JsonValueKind.Array => value.EnumerateArray().Any(v => Status(v, depth + 1)),
            JsonValueKind.String => EncodedStatus(value.GetString()!, depth + 1),
            _ => false
        });
        bool EncodedStatus(string text, int depth)
        {
            if (depth > 32) return false;
            JsonDocument? doc;
            try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException) { return false; }
            using (doc) return Status(doc.RootElement, depth);
        }
        return reply.Result.StructuredContent is JsonElement data && Status(data, 0) ||
            reply.Result.Content.OfType<TextContentBlock>().Any(t => EncodedStatus(t.Text, 0));
    }
}
