using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using ModelContextProtocol.Protocol;

namespace WorkIqFiles;

internal static class SafeWorkIqDiagnostic
{
    private static readonly HashSet<string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        "accessDenied", "Authorization_RequestDenied", "Forbidden", "Unauthorized", "InvalidAuthenticationToken",
        "itemNotFound", "ResourceNotFound", "Request_ResourceNotFound", "notFound", "invalidRequest",
        "BadRequest", "InvalidArgument", "notSupported", "NotImplemented", "TooManyRequests",
        "activityLimitReached", "throttledRequest", "serviceNotAvailable", "ServiceUnavailable",
        "generalException", "InternalServerError", "UnknownError", "PolicyViolation", "insufficient_claims"
    };
    internal static string ResourceFailures(CallToolResult result)
    {
        List<string> failures = [];
        int nodes = 0, chars = 0;
        bool limited = false;
        void Parse(string text, int depth)
        {
            if ((chars += text.Length) > 262144) { limited = true; return; }
            if (!text.TrimStart().StartsWith('{') && !text.TrimStart().StartsWith('[')) return;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
                Visit(doc.RootElement, depth);
            }
            catch (JsonException) { } // Only structured status/code evidence is eligible.
        }
        void Visit(JsonElement value, int depth)
        {
            if (++nodes > 128 || depth > 16 || failures.Count >= 8) { limited = true; return; }
            if (value.ValueKind == JsonValueKind.Object)
            {
                int? status = Status(Property(value, "statusCode"));
                if (status is >= 200 and < 300) return;
                JsonElement error = Property(value, "error");
                if (error.ValueKind == JsonValueKind.Undefined) error = Property(Property(value, "data"), "error");
                if (status is >= 400 || error.ValueKind is JsonValueKind.Object or JsonValueKind.String)
                {
                    for (int i = 0; i < 3 && Property(error, "error").ValueKind == JsonValueKind.Object; i++)
                        error = Property(error, "error");
                    string? code = String(Property(error, "code"));
                    code = code is not null && Codes.Contains(code)
                        ? Codes.First(c => c.Equals(code, StringComparison.OrdinalIgnoreCase)) : null;
                    failures.Add($"resource-error[{failures.Count + 1}] status[{status?.ToString() ?? "unavailable"}] code[{code ?? "unavailable"}]" +
                        $"; providerReason[{ReasonCategory(String(Property(error, "message")))}]");
                    return;
                }
                foreach (string wrapper in new[] { "results", "result", "response", "body", "data", "content", "text", "structuredContent" })
                    if (Property(value, wrapper).ValueKind != JsonValueKind.Undefined) Visit(Property(value, wrapper), depth + 1);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray())
                {
                    Visit(item, depth + 1);
                    if (nodes > 128 || failures.Count >= 8) { limited = true; break; }
                }
            }
            else if (value.ValueKind == JsonValueKind.String) Parse(value.GetString()!, depth + 1);
        }
        if (result.StructuredContent is JsonElement structured) Parse(structured.GetRawText(), 0);
        else foreach (TextContentBlock text in result.Content.OfType<TextContentBlock>().Take(4)) Parse(text.Text, 0);
        return (failures.Count == 0 ? "resource status/code unavailable" : string.Join("; ", failures)) +
            (limited ? "; diagnostic detail bound reached" : "") +
            "; envelope-reported status, not independently verified downstream cause";
    }
    internal static string ReasonCategory(string? message)
    {
        if (message is null || message.Length > 4096) return "unavailable";
        // Fixed phrases only; no captured names, types, paths or messages are echoed.
        string text = message.ToLowerInvariant();
        if (text.Contains("could not find a property named") || text.Contains("does not exist on type") ||
            text.Contains("undeclared property")) return "unsupported-property-reported";
        if (text.Contains("could not be resolved by the model") || text.Contains("unknown type name") ||
            text.Contains("invalid odata type")) return "invalid-type-reported";
        if (text.Contains("required property") && (text.Contains("missing") || text.Contains("not found")))
            return "missing-required-property-reported";
        if (text.Contains("mention") && (text.Contains("does not match") || text.Contains("not found in") ||
            text.Contains("invalid mention"))) return "mention-binding-reported";
        if (text.Contains("insufficient privileges") || text.Contains("access is denied")) return "authorization-reported";
        if (text.Contains("resource not found for the segment")) return "invalid-path-reported";
        return "unclassified";
    }
    internal static string Describe(CallToolResult result, int? outerStatus, bool toolError, bool includeDetails = false)
    {
        JsonElement envelope = default;
        if (result.StructuredContent is JsonElement structured) envelope = structured;
        else if (result.Content.Count == 1 && result.Content[0] is TextContentBlock text && text.Text.Length <= 262144)
        {
            try { using JsonDocument doc = JsonDocument.Parse(text.Text); envelope = doc.RootElement.Clone(); }
            catch (JsonException) { } // Free-form backend text is deliberately not surfaced.
        }
        if (envelope.ValueKind == JsonValueKind.Object && envelope.GetRawText().Length > 262144) envelope = default;
        JsonElement resource = envelope;
        if (Property(envelope, "results") is { ValueKind: JsonValueKind.Array } results && results.GetArrayLength() == 1)
            resource = results[0];
        int? status = Status(Property(resource, "statusCode"));
        JsonElement error = Property(resource, "error");
        if (error.ValueKind != JsonValueKind.Object) error = Property(Property(resource, "data"), "error");
        for (int depth = 0; depth < 3 && Property(error, "error").ValueKind == JsonValueKind.Object; depth++)
            error = Property(error, "error");
        string? code = String(Property(error, "code")) ?? String(Property(resource, "code"));
        code = code is not null && Codes.Contains(code) ? Codes.First(c => c.Equals(code, StringComparison.OrdinalIgnoreCase)) : null;
        JsonElement inner = Property(error, "innerError");
        if (inner.ValueKind != JsonValueKind.Object) inner = Property(error, "innererror");
        string? correlation = String(Property(inner, "request-id")) ?? String(Property(resource, "requestId"));
        // GUID format only, from explicit request-ID slots; never copy messages, URLs or arbitrary strings.
        correlation = Guid.TryParseExact(correlation, "D", out Guid id) && id != Guid.Empty ? id.ToString("D") : null;
        string summary = $"WorkIQ diagnostic: operation=channel-filesFolder; tool=fetch; path=/teams/{{teamId}}/channels/{{channelId}}/filesFolder; stage={(toolError ? "in-band MCP tool error" : "in-band resource error")}; " +
            $"outerHttp={outerStatus?.ToString() ?? "unavailable"}; resourceStatus={status?.ToString() ?? "unavailable"}; " +
            $"errorCode={code ?? "unavailable"}; requestId={correlation ?? "unavailable"}.\n" +
            "resourceStatus is reported by the WorkIQ envelope, not independently identified as a Graph downstream status. " +
            "AU bearer was sent and a tool response received; this does not establish resource authorization. " +
            "Unlisted/free-form details withheld. No alternate identity or permission change; cause is not inferred.";
        if (!includeDetails) return summary;
        string? message = String(Property(error, "message")) ?? String(Property(resource, "message")) ??
            String(Property(resource, "error"));
        if (message is null && toolError && result.Content.Count == 1 && result.Content[0] is TextContentBlock block &&
            envelope.ValueKind == JsonValueKind.Undefined &&
            !block.Text.TrimStart().StartsWith('{') && !block.Text.TrimStart().StartsWith('[')) message = block.Text;
        string? rawCode = String(Property(error, "code")) ?? String(Property(resource, "code"));
        string? innerMessage = String(Property(inner, "message"));
        return summary.Replace("Unlisted/free-form details withheld.", "Opt-in redacted backend error details (untrusted):") +
            "\nerror.code=" + Scrub(rawCode ?? "unavailable") +
            "\nerror.message=" + Scrub(message ?? "unavailable") +
            "\nerror.innerError.message=" + Scrub(innerMessage ?? "unavailable") +
            (toolError ? "\n" + ErrorStructure(result) : "");
    }
    internal static string ErrorStructure(CallToolResult result)
    {
        if (result.IsError != true) return "errorStructure=not an error; omitted";
        List<string> lines = [];
        int remaining = 6000, nodes = 0;
        void Add(string line)
        {
            if (remaining <= 0) return;
            string safe = line.Length > remaining ? line[..remaining] : line;
            lines.Add(safe); remaining -= safe.Length + 1;
        }
        bool Sensitive(string key) => Regex.IsMatch(key,
            "token|authorization|cookie|secret|password|assertion|code_verifier|headers|credential|download|webUrl|sharingUrl",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        void Visit(JsonElement value, string path, int depth, bool showText = false)
        {
            if (++nodes > 70 || depth > 7 || remaining <= 0) { Add(path + "=[limit]"); return; }
            Add(path + ":" + value.ValueKind);
            if (value.ValueKind == JsonValueKind.Object)
            {
                // An isError tool envelope can still contain successful entity results. Never dump those.
                int? localStatus = Status(Property(value, "statusCode")) ?? Status(Property(value, "status"));
                if (localStatus is >= 200 and < 300) { Add(path + "=[successful resource omitted]"); return; }
                foreach (JsonProperty property in value.EnumerateObject().Take(20))
                {
                    string key = Regex.IsMatch(property.Name, @"\A[@A-Za-z][A-Za-z0-9_.@-]{0,63}\z",
                        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ? property.Name : "[field]";
                    string child = path + "." + key;
                    if (Sensitive(property.Name)) { Add(child + "=[sensitive field omitted]"); continue; }
                    string lower = property.Name.ToLowerInvariant();
                    bool message = lower is "message" or "detail" or "title" or "description" or "reason" or "text" or "error_description";
                    bool wrapper = lower is "error" or "errors" or "body" or "content" or "data" or "response" or "results" or "innererror" or "details";
                    bool scalar = lower is "status" or "statuscode" or "code" or "type";
                    if (message || wrapper || scalar)
                        Visit(property.Value, child, depth + 1, message || lower is "error" or "body" or "content");
                    else Add(child + ":" + property.Value.ValueKind + " [value omitted]");
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement item in value.EnumerateArray().Take(4)) Visit(item, path + "[" + index++ + "]", depth + 1, showText);
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()!;
                string trimmed = text.TrimStart();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                {
                    if (text.Length > 262144) { Add(path + "=[oversized encoded structure omitted]"); return; }
                    try { using JsonDocument doc = JsonDocument.Parse(text); Visit(doc.RootElement, path + ".json", depth + 1); }
                    catch (JsonException) { Add(path + "=[malformed encoded structure omitted]"); }
                }
                else if (showText || path.EndsWith(".code") || path.EndsWith(".type"))
                    Add(path + "=" + Scrub(text));
            }
            else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                Add(path + "=" + number);
        }
        if (result.StructuredContent is JsonElement structured) Visit(structured, "structuredContent", 0);
        else Add("structuredContent=absent");
        int blockIndex = 0;
        foreach (ContentBlock block in result.Content.Take(4))
        {
            string path = "content[" + blockIndex++ + "]";
            if (block is TextContentBlock text) Visit(JsonSerializer.SerializeToElement(text.Text), path + ".text", 0, true);
            else Add(path + "=[non-text block omitted]");
        }
        return string.Join("\n", lines) + (remaining <= 0 || nodes > 70 ? "\n[diagnostic truncated]" : "");
    }
    internal static string ResolverErrorDetails(CallToolResult result)
    {
        string? details = null;
        int nodes = 0, bytes = 0;
        void Visit(JsonElement value, int depth)
        {
            if (details is not null || ++nodes > 80 || depth > 8) return;
            if (value.ValueKind == JsonValueKind.Object)
            {
                int? status = Status(Property(value, "statusCode"));
                // Never reveal successful resource strings, even within an isError/mixed response.
                if (status is >= 200 and < 300) return;
                JsonElement error = Property(value, "error");
                if (error.ValueKind == JsonValueKind.Object &&
                    (result.IsError == true || status is >= 400))
                {
                    for (int i = 0; i < 3 && Property(error, "error").ValueKind == JsonValueKind.Object; i++)
                        error = Property(error, "error");
                    JsonElement inner = Property(error, "innerError");
                    if (inner.ValueKind != JsonValueKind.Object) inner = Property(error, "innererror");
                    string? message = String(Property(error, "message"));
                    string? innerMessage = String(Property(inner, "message"));
                    string? code = String(Property(error, "code"));
                    code = code is not null && Codes.Contains(code)
                        ? Codes.First(c => c.Equals(code, StringComparison.OrdinalIgnoreCase)) : null;
                    string? request = String(Property(inner, "request-id")) ?? String(Property(value, "requestId"));
                    request = Guid.TryParseExact(request, "D", out Guid id) && id != Guid.Empty ? id.ToString("D") : null;
                    details = "REDACTED RESOLVER ERROR ONLY (untrusted backend text): " +
                        $"resourceStatus={status?.ToString() ?? "unavailable"}; errorCode={code ?? "unlisted/withheld"}; requestId={request ?? "unavailable"}.\n" +
                        "error.message=" + (message is null ? "unavailable" : Scrub(message, maxOutput: 1000)) + "\n" +
                        "error.innerError.message=" + (innerMessage is null ? "unavailable" : Scrub(innerMessage, maxOutput: 350)) + "\n" +
                        "Status does not establish permission/authentication cause or absence of service effects. No automatic retry.\n";
                    return;
                }
                foreach (string wrapper in new[] { "results", "result", "response", "body", "data", "content", "text", "structuredContent" })
                    if (Property(value, wrapper).ValueKind != JsonValueKind.Undefined) Visit(Property(value, wrapper), depth + 1);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray().Take(4)) Visit(item, depth + 1);
            }
            else if (value.ValueKind == JsonValueKind.String) Parse(value.GetString()!, depth + 1);
        }
        void Parse(string text, int depth)
        {
            if (depth > 8 || (bytes += System.Text.Encoding.UTF8.GetByteCount(text)) > BoundedBlobResponse.MaxEnvelopeBytes) return;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
                Visit(doc.RootElement, depth);
            }
            catch (JsonException) { } // No free-form response dump: it may include success data or keys.
        }
        if (result.StructuredContent is JsonElement structured) Parse(structured.GetRawText(), 0);
        foreach (TextContentBlock block in result.Content.Take(4).OfType<TextContentBlock>())
            if (details is null) Parse(block.Text, 0);
        return details ?? "";
    }

    internal static string Scrub(string text, int maxInput = 8192, int maxOutput = 1600, bool scrubResourcePaths = true)
    {
        if (text.Length > maxInput) text = text[..maxInput] + " [truncated]";
        text = WebUtility.HtmlDecode(text).Replace("\\/", "/", StringComparison.Ordinal);
        // The same patterns scrub large native schemas; keep matching linear without relaxing the timeout.
        string Replace(string pattern, string replacement) => Regex.Replace(text, pattern, replacement,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
        // Drop credential assignments through end-of-line rather than risk preserving a spaced value.
        text = Replace(@"[""']?(?:authorization|proxy-authorization|cookie|set-cookie|client[_ -]?secret|access[_ -]?token|refresh[_ -]?token|id[_ -]?token|client[_ -]?assertion|oauth[_ -]?code|authorization[_ -]?code|code_verifier|password|secret|token|code)[""']?\s*[:=]\s*[^\r\n]*", "[credential redacted]");
        text = Replace(@"\b(?:Bearer|Basic)\s+[^\s,;]+", "[authorization redacted]");
        text = Replace(@"(?:https?://|https?%3a%2f%2f)[^\s<>""']+", "[URL redacted]");
        text = Replace(@"\b(?:site[_ -]?key|opaque[_ -]?key)\b[^\r\n]*", "[site-key detail redacted]");
        if (scrubResourcePaths) text = Replace(@"/(?:sites|teams|personal)/[^\s<>""']+", "[SharePoint path redacted]");
        text = Replace(@"\b(?:code|token|sig|signature|secret)\s*=\s*[^\s&]+", "[credential redacted]");
        text = Replace(@"\beyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]*)?", "[JWT redacted]");
        text = Replace(@"[A-Za-z0-9_+/=-]{64,}", "[opaque value redacted]");
        text = Regex.Replace(text, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return text.Length > maxOutput ? text[..maxOutput] + " [truncated]" : text;
    }
    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement found) ? found : default;
    private static string? String(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Status(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int status) && status is >= 100 and <= 599 ? status : null;
}
