using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Teams.Apps.Schema;
using ModelContextProtocol.Protocol;

namespace WorkIqFiles;

// Per invocation, display-copy only. Never feeds model context, progress cards or shared logging.
internal sealed class DirectPrivateErrorReceipt
{
    internal const string Marker = "\nPrivate WorkIQ returned-error excerpt";
    private readonly List<string> rows = [];
    private int chars;
    private bool limited;
    internal static bool Personal(MessageActivity activity, Invocation invocation) =>
        invocation.Type == "personal" && activity.Conversation?.ConversationType == "personal" &&
        activity.Conversation.IsGroup != true && invocation.Conversation == activity.Conversation.Id;

    internal void Failure(int call, Exception error) => Add(new
    {
        call, source = "local call boundary",
        kind = error is ModelContextProtocol.McpException ? "thrown MCP protocol exception" :
            error is HttpRequestException ? "thrown transport exception" : "local exception",
        returnedBody = "unavailable; exception text not copied",
        status = error is HttpRequestException http ? ((int?)http.StatusCode)?.ToString() ?? "unavailable" : "unavailable"
    });

    private void Add(object row)
    {
        string text = JsonSerializer.Serialize(row);
        if (rows.Count >= 8 || chars + text.Length > 4800) { limited = true; return; }
        rows.Add(text); chars += text.Length;
    }
    internal string Receipt() => rows.Count == 0 && !limited ? "" :
        Marker + " (allowlisted, sanitized/inert display copy; NOT full raw HTTP or model explanation):\n" +
        string.Join("\n", rows) + $"\nexcerptLimitReached={limited.ToString().ToLowerInvariant()}; " +
        "unlisted fields omitted; absent fields/IDs unavailable; no downstream cause inferred; no retry.\n";

    private static string Display(string value)
    {
        try { return DisplayCore(value); }
        catch (RegexMatchTimeoutException) { return "sanitization unavailable; value withheld"; }
    }
    private static string DisplayCore(string value)
    {
        // Extend the shared scrubber for key assignments; scrub only this new display copy.
        string input = System.Net.WebUtility.HtmlDecode(value.Length > 8192 ? value[..8192] + " [truncated]" : value);
        static string Assignments(string text) => Regex.Replace(text,
            @"[""']?(?:api[_ -]?key|subscription[_ -]?key|x-api-key|authorization|proxy-authorization|cookie|set-cookie|client[_ -]?secret|access[_ -]?token|refresh[_ -]?token|id[_ -]?token|client[_ -]?assertion|password|secret|token|sig|signature)[""']?[ \t]*[:=][ \t]*[^\r\n]*",
            "[credential redacted]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100));
        string safe = Assignments(SafeWorkIqDiagnostic.Scrub(Assignments(input), maxOutput: 600));
        // Literal escape spelling remains inert even in a Markdown-capable delivery surface.
        return string.Concat(safe.Select(c => "<>&@[]()`*_!#".Contains(c) || char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format
            ? "\\u" + ((int)c).ToString("X4") : c.ToString()));
    }
    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    private static int? Status(JsonElement value)
    {
        JsonElement status = Property(value, "statusCode");
        if (status.ValueKind == JsonValueKind.Undefined) status = Property(value, "status");
        return status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int n) && n is >= 100 and <= 599 ? n : null;
    }
    internal void Observe(int call, JsonElement arguments, CallToolResult result, bool errorReported)
    {
        int nodes = 0, inputChars = 0, before = rows.Count;
        string[] requests = Property(arguments, "entityUrls") is { ValueKind: JsonValueKind.Array } urls
            ? urls.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).ToArray() : [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        void Note(string source, int? item, string observation) =>
            Add(new { call, resultItem = item, source, observation });
        void Fields(JsonElement value, string path, Dictionary<string, string> fields, int depth)
        {
            if (++nodes > 256 || depth > 10 || fields.Count >= 16) { limited = true; return; }
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()!;
                if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
                {
                    if (text.Length > 8192) { limited = true; return; }
                    try { using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 }); Fields(doc.RootElement, path + ".json", fields, depth + 1); }
                    catch (JsonException) { fields[path + ".observation"] = "malformed-json-unavailable"; }
                }
                else fields[path] = Display(text);
                return;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var child in value.EnumerateArray())
                { if (i >= 4) { limited = true; break; } Fields(child, path + "[" + i++ + "]", fields, depth + 1); }
                return;
            }
            if (value.ValueKind != JsonValueKind.Object) return;
            if (Status(value) is >= 200 and < 300) return;
            if (value.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            { fields[path + ".observation"] = "duplicate-fields-unavailable"; return; }
            foreach (var property in value.EnumerateObject())
            {
                string key = property.Name.ToLowerInvariant();
                string child = path + "." + key;
                if (key is "code" or "message" or "detail" or "reason" or "error_description")
                {
                    if (property.Value.ValueKind == JsonValueKind.String) fields[child] = Display(property.Value.GetString()!);
                    else if (key == "code" && property.Value.ValueKind == JsonValueKind.Number)
                        fields[child] = Display(property.Value.GetRawText());
                }
                else if (key is "request-id" or "requestid" or "client-request-id" or "clientrequestid" or
                    "correlation-id" or "correlationid" or "trace-id" or "traceid")
                {
                    string? id = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    fields[child] = id is not null && Regex.IsMatch(id, @"\A[A-Za-z0-9-]{1,128}\z",
                        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100))
                        ? Display(id) : "unavailable-invalid-id";
                }
                else if (key is "error" or "innererror" or "details" or "body")
                    Fields(property.Value, child, fields, depth + 1);
                if (fields.Count >= 16) { limited = true; break; }
            }
        }
        void Read(JsonElement node, string source, int? item, int depth, string? attribution = null, int? inheritedStatus = null)
        {
            if (++nodes > 256 || depth > 10) { limited = true; return; }
            if (node.ValueKind == JsonValueKind.String)
            { Parse(node.GetString()!, source + "/json", item, depth + 1, attribution, inheritedStatus); return; }
            if (node.ValueKind != JsonValueKind.Object) return;
            if (node.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            { Note(source, item, "duplicate-fields-unavailable"); return; }
            int? status = Status(node) ?? inheritedStatus;
            if (status is >= 200 and < 300) return; // Do not extract successful resource/business bodies.
            string[] echoes = new[] { "entityUrl", "url" }.Where(k => Property(node, k).ValueKind != JsonValueKind.Undefined)
                .Select(k => Property(node, k).ValueKind == JsonValueKind.String ? Property(node, k).GetString()! : "").ToArray();
            if (echoes.Length > 0)
            {
                string? match = requests.Distinct(StringComparer.Ordinal).SingleOrDefault(r => echoes.All(e => e == r));
                attribution = match is not null && (attribution is null || attribution == match) ? match : "!conflicting";
            }
            JsonElement error = Property(node, "error");
            if (error.ValueKind is JsonValueKind.Object or JsonValueKind.String || status is >= 400)
            {
                Dictionary<string, string> fields = new(StringComparer.Ordinal);
                Fields(error.ValueKind == JsonValueKind.Undefined ? node : error, "error", fields, 0);
                if (Property(node, "data") is { ValueKind: JsonValueKind.Object or JsonValueKind.String } data)
                {
                    if (data.ValueKind == JsonValueKind.String)
                    {
                        string text = data.GetString()!;
                        if (text.Length > 8192) { limited = true; }
                        else
                        {
                            try { using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 }); Fields(doc.RootElement, "data", fields, 0); }
                            catch (JsonException)
                            {
                                if (!text.TrimStart().StartsWith('{') && !text.TrimStart().StartsWith('['))
                                    fields["data.error-text"] = Display(text);
                                else fields["data.observation"] = "malformed-json-unavailable";
                            }
                        }
                    }
                    else Fields(data, "data", fields, 0);
                }
                // Header values are not enumerated; only explicit correlation slots are admitted.
                foreach (var owner in new[] { (Value: node, Path: "envelope"), (Value: Property(node, "headers"), Path: "headers") })
                {
                    if (owner.Value.ValueKind != JsonValueKind.Object) continue;
                    if (owner.Value.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                    { fields[owner.Path + ".observation"] = "duplicate-fields-unavailable"; continue; }
                    foreach (string key in new[] { "request-id", "requestId", "client-request-id", "clientRequestId",
                        "correlation-id", "correlationId", "trace-id", "traceId" })
                        if (Property(owner.Value, key) is { ValueKind: JsonValueKind.String } id)
                            Fields(JsonSerializer.SerializeToElement(new Dictionary<string, string> { [key] = id.GetString()! }), owner.Path, fields, 0);
                }
                string fingerprint = JsonSerializer.Serialize(new { item, status, fields });
                if (!seen.Add(fingerprint)) { Note(source, item, "duplicate-error-copy-omitted"); return; }
                Add(new { call, resultItem = item, source,
                    kind = "provider-returned error portion", mcpIsError = result.IsError == true,
                    status = status?.ToString() ?? "unavailable",
                    resourceAttribution = attribution == "!conflicting" ? "conflicting" : attribution is null ? "unavailable-not-inferred-from-position" : "matched-request",
                    resourceSha256 = attribution is not null && attribution != "!conflicting" ? ContentHash.Hash(attribution) : "unavailable",
                    fields, transformation = "allowlisted; credential/URL scrubbed; inert escapes; values capped600 before escaping; missing code/message/IDs unavailable" });
                return;
            }
            if (Property(node, "results") is { ValueKind: JsonValueKind.Array } results)
            {
                int i = 0;
                foreach (var child in results.EnumerateArray())
                {
                    if (nodes >= 256) { limited = true; break; }
                    Read(child, source + "/results[" + ++i + "]", i, depth + 1, results.GetArrayLength() == 1 ? attribution : null);
                }
            }
            if (Property(node, "content") is { ValueKind: JsonValueKind.Array } content)
            {
                int i = 0;
                foreach (var block in content.EnumerateArray())
                {
                    if (++i > 4) { limited = true; break; }
                    if (Property(block, "type") is { ValueKind: JsonValueKind.String } type &&
                        type.GetString() == "text" && Property(block, "text") is { ValueKind: JsonValueKind.String } text)
                        Parse(text.GetString()!, source + "/content[" + i + "]/text", item, depth + 1, attribution, status);
                }
            }
            foreach (string key in new[] { "structuredContent", "data", "result", "response" })
                if (Property(node, key).ValueKind != JsonValueKind.Undefined)
                    Read(Property(node, key), source + "/" + key, item, depth + 1, attribution, status);
        }
        void Parse(string text, string source, int? item, int depth, string? attribution, int? status = null)
        {
            if ((inputChars += text.Length) > 262144) { limited = true; return; }
            try { using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 }); Read(doc.RootElement, source, item, depth, attribution, status); }
            catch (JsonException)
            {
                if (result.IsError == true && !text.TrimStart().StartsWith('{') && !text.TrimStart().StartsWith('['))
                    Add(new { call, resultItem = item, source, kind = "MCP isError text (not verified resource/HTTP status)", message = Display(text),
                        transformation = "sanitized/inert bounded display copy" });
                else if (errorReported) Note(source, item, "malformed-or-non-json-error-unavailable");
            }
        }
        string? single = requests.Length == 1 ? requests[0] : null;
        if (result.StructuredContent is { } structured)
        {
            if (structured.ValueKind == JsonValueKind.Undefined) Note("structuredContent", null, "undefined-payload-unavailable");
            else Parse(structured.GetRawText(), "structuredContent", null, 0, single);
        }
        int block = 0;
        foreach (var content in result.Content ?? [])
        {
            if (++block > 4) { limited = true; break; }
            if (content is TextContentBlock text)
            {
                if (text.Text is null) Note("content[" + block + "]/text", null, "null-text-unavailable");
                else Parse(text.Text, "content[" + block + "]/text", null, 0, single);
            }
        }
        if (errorReported && rows.Count == before)
            Note("returned MCP result", null, "error reported; eligible body/code/message/IDs unavailable");
    }
}
