using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkIqFiles;

// Provider errors can echo entire requests. Never copy a message, unknown code, or arbitrary field name.
internal static class AzureModelDiagnostic
{
    internal const int MaxBytes = 16384;
    private static readonly string[] Codes =
    [
        "invalid_request_error", "invalid_function_parameters", "invalid_value", "unsupported_parameter",
        "unsupported_value", "model_not_found", "DeploymentNotFound", "OperationNotSupported",
        "InvalidArgument", "BadRequest", "InvalidRequest", "content_filter", "ResponsibleAIPolicyViolation",
        "context_length_exceeded", "invalid_api_key", "Unauthorized", "PermissionDenied",
        "rate_limit_exceeded", "insufficient_quota", "server_error", "InternalServerError"
    ];
    private static readonly string[] Types =
        ["invalid_request_error", "authentication_error", "permission_error", "rate_limit_error", "server_error"];
    private static readonly HashSet<string> Fields =
    [
        "tools", "function", "functions", "parameters", "properties", "items", "type", "required",
        "additionalProperties", "strict", "anyOf", "oneOf", "enum", "$schema", "$ref",
        "minLength", "maxLength", "minItems", "maxItems", "name", "description", "default",
        "entityUrls", "agentId", "path", "query", "format", "operationType",
        "model", "messages", "role", "content", "tool_calls", "tool_call_id",
        "temperature", "top_p", "max_tokens", "max_completion_tokens", "reasoning_effort",
        "response_format", "stream", "parallel_tool_calls", "tool_choice"
    ];
    internal static async Task<string> Read(HttpResponseMessage response, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string bodyState = "structured", code = "unavailable", type = "unavailable", param = "unavailable";
        string classification = "unclassified", parameterEvidence = "unavailable", reportedValues = "unavailable";
        string requestId = "unavailable";
        foreach (string header in new[] { "apim-request-id", "x-request-id", "x-ms-request-id" })
        {
            if (!response.Headers.TryGetValues(header, out IEnumerable<string>? values)) continue;
            string[] candidates = values.Take(2).ToArray();
            if (candidates.Length == 1 && Guid.TryParseExact(candidates[0], "D", out Guid id) && id != Guid.Empty)
            { requestId = id.ToString("D"); break; }
        }
        try
        {
            if (response.Content.Headers.ContentLength > MaxBytes) bodyState = "oversized-withheld";
            else
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
                using MemoryStream buffer = new();
                byte[] chunk = new byte[2048];
                while (true)
                {
                    int read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaxBytes + 1 - (int)buffer.Length)), ct);
                    if (read == 0) break;
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaxBytes) { bodyState = "oversized-withheld"; break; }
                }
                if (bodyState == "structured")
                {
                    using JsonDocument doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out JsonElement error) ||
                        error.ValueKind != JsonValueKind.Object ||
                        error.EnumerateObject().Select(p => p.Name).Distinct().Count() != error.EnumerateObject().Count())
                        bodyState = "unrecognized-withheld";
                    else
                    {
                        code = Allowed(String(error, "code"), Codes);
                        type = Allowed(String(error, "type"), Types);
                        param = Parameter(String(error, "param"));
                        string? message = String(error, "message");
                        classification = Classify(code, message);
                        if (param == "unavailable" && message is { Length: <= 4096 })
                        {
                            // Only a named parameter in known provider phrases; never arbitrary quoted text.
                            Match match = Regex.Match(message,
                                @"\b(?:Unsupported parameter|Unsupported value|Invalid parameter):\s*'([^'\r\n]{1,200})'",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                            if (match.Success) param = Parameter(match.Groups[1].Value);
                        }
                        if (param == "reasoning_effort" && message is { Length: <= 4096 })
                            (parameterEvidence, reportedValues) = ReasoningEvidence(message);
                    }
                }
            }
        }
        catch (JsonException) { bodyState = "malformed-withheld"; }
        catch (Exception error) when (error is IOException or HttpRequestException)
        { bodyState = "unreadable-withheld"; }
        ct.ThrowIfCancellationRequested();
        PrivateIngressDiagnostic.ObserveModelError(code, type, param, classification);
        return $"Azure model HTTP {(int)response.StatusCode}; error.code [{code}]; error.type [{type}]; " +
            $"error.param [{param}]; classification [{classification}]; requestId [{requestId}]; body [{bodyState}]. " +
            (param == "reasoning_effort" ? $"parameterEvidence [{parameterEvidence}]; reportedValues [{reportedValues}]. " : "") +
            Hint(classification) + " Provider messages/bodies withheld; classification is an observation, not a verified root cause. " +
            "HTTP 400 does not establish an invalid API key. No retry/fallback.";
    }
    private static string? String(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Allowed(string? value, string[] allowed) =>
        value is { Length: <= 64 } ? allowed.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? "withheld-or-unknown" :
            "unavailable";
    internal static string Parameter(string? value)
    {
        if (value is null) return "unavailable";
        if (value.Length is < 1 or > 200 || !Regex.IsMatch(value,
            @"\A[A-Za-z_$][A-Za-z0-9_$]*(?:\[[0-9]{1,2}\]|\.(?:[A-Za-z_$][A-Za-z0-9_$]*|[0-9]{1,2}))*\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return "withheld-or-unknown";
        string[] parts = value.Replace("[", ".", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal).Split('.');
        return parts.All(p => Fields.Contains(p) || p.All(char.IsAsciiDigit)) ? value : "withheld-or-unknown";
    }
    private static string Classify(string code, string? message)
    {
        if (code == "invalid_function_parameters") return "tool-schema-rejected";
        if (code is "unsupported_parameter" or "unsupported_value") return "parameter-rejected";
        if (code is "model_not_found" or "DeploymentNotFound") return "deployment-not-found";
        if (code == "OperationNotSupported") return "operation-not-supported";
        if (code is "content_filter" or "ResponsibleAIPolicyViolation") return "content-policy";
        if (code == "context_length_exceeded") return "context-limit";
        if (code is "invalid_api_key" or "Unauthorized" or "PermissionDenied") return "authentication-or-permission";
        if (message is not { Length: <= 4096 }) return "unclassified";
        if (message.StartsWith("Function tools with reasoning_effort are not supported", StringComparison.OrdinalIgnoreCase))
            return "reasoning-tools-incompatible";
        if (Regex.IsMatch(message, @"\AInvalid schema for (?:function|tool)\b", RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100))) return "tool-schema-rejected";
        if (message.StartsWith("Unsupported parameter:", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Unsupported value:", StringComparison.OrdinalIgnoreCase)) return "parameter-rejected";
        return "unclassified";
    }
    private static (string Evidence, string Values) ReasoningEvidence(string message)
    {
        const string value = "(?:none|minimal|low|medium|high|xhigh|max)";
        bool Is(string pattern) => Regex.IsMatch(message, pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        string evidence =
            message.StartsWith("Function tools with reasoning_effort are not supported", StringComparison.OrdinalIgnoreCase) ? "tools-reasoning-combination-unsupported" :
            Is(@"\A(?:Missing required parameter:\s*'reasoning_effort'|'?reasoning_effort'?\s+is required\b)") ? "missing-required" :
            Is(@"\AUnsupported parameter:\s*'reasoning_effort'") ? "unsupported-parameter" :
            Is(@"\A(?:Unsupported|Invalid) (?:default(?: value)?):.*\breasoning_effort\b") ? "unsupported-default" :
            Is(@"\A(?:Unsupported|Invalid) value:") ? "unsupported-value" : "unclassified";
        List<string> values = [];
        // Only literal enum tokens from tightly scoped provider clauses. These are not deployment guarantees.
        foreach (Match match in Regex.Matches(message,
            @"(?:Supported values (?:are|include):?\s*|set reasoning_effort to\s*)" +
            @"(?<values>'" + value + @"'(?:\s*,\s*'" + value + @"')*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            foreach (Match token in Regex.Matches(match.Groups["values"].Value, value,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                values.Add(token.Value.ToLowerInvariant());
        return (evidence, values.Count == 0 ? "unavailable" : string.Join(",", values.Distinct().Take(7)));
    }
    private static string Hint(string classification) => classification switch
    {
        "tool-schema-rejected" => "Inspect the named function/schema field in the offline payload; do not weaken resource guards.",
        "parameter-rejected" => "Inspect the named request parameter and deployment API contract.",
        "deployment-not-found" => "Verify the configured deployment exists at this endpoint.",
        "operation-not-supported" => "Verify this deployment supports Chat Completions and function tools.",
        "reasoning-tools-incompatible" => "Provider reports tools/reasoning incompatibility. Verify the deployment contract before choosing a supported effort or Responses API; no automatic change.",
        "context-limit" => "Narrow the request; the service reported a context limit.",
        "content-policy" => "The service reported a content-policy rejection; no policy bypass.",
        "authentication-or-permission" => "Review credentials/resource permission privately; do not paste keys.",
        _ => "The cause remains undetermined; retain these safe fields for diagnosis."
    };
}
