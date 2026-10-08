using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WorkIqFiles;

internal sealed class DirectMcpOptions
{
    public bool Enabled { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseTeamsMcp { get; set; }
    public string Principal { get; set; } = "AgentUser";
    internal void Validate()
    {
        if (!UseTeamsMcp && Principal is not ("AgentUser" or "SignedInHuman"))
            throw new LabException("Invalid DirectMcp profile.");
    }
}

internal sealed record DirectDuplicateField(string Key, string KeyClass, int Occurrences, int ObjectDepth,
    string Source, bool RawValuesIdentical)
{
    internal string Diagnostic => $"duplicateKey={Key}; keyClass={KeyClass}; occurrences={Occurrences}; " +
        $"objectDepth={ObjectDepth}; source={Source}; rawValuesIdentical={RawValuesIdentical}. " +
        "Values withheld; argument display withheld because normalized JSON hides duplicate fields.";
}

internal static class DirectMcpContract
{
    internal const string ArgumentGuidance =
        "Every JSON object must have unique property names, including objects in encoded jsonBody. " +
        "For collection arguments such as entityUrls, use one property containing the array, not repeated properties. " +
        "Duplicate names are rejected even when their values are identical; the client never chooses first or last.";
    internal const string PrincipalAndTransferGuidance = """
        /me refers to the fixed authenticated WorkIQ principal, not automatically to the person sending the request.
        In AgentUser profile, /me/drive is the AU's own drive; do not call it the human requester's personal drive.
        In SignedInHuman profile, /me refers to the verified matched human. Never switch or fall back between these identities.
        Teams attachment metadata is not file content. Reattaching a file or providing a link does not guarantee
        source access: this client does not ingest attachment bytes or download attachment/provider URLs.
        A native upload requires established source content and a supported native transfer contract entirely within WorkIQ.
        Do not invent source bytes or claim an exact copy from a name, metadata, summary or model-reconstructed content.
        Upload preparation or an external upload URL is not a completed upload. If external byte transfer is required,
        report that missing capability as unsupported here; do not promise that a more accessible URL fixes it.
        Native schema discovery alone proves neither source access nor end-to-end transfer support.
        """;
    internal const string MentionGuidance = """
        For a requested PERSON mention in an existing chat, select the advertised native create_entity tool
        with parentUrl "/chats/{resolved-existing-chat-id}/messages" and native jsonBody.
        Choose advertised discovery/read tools as needed to resolve the requested person and existing chat.
        Do not guess identity from display names or decode chat/member IDs into directory user IDs.
        For a native Teams mention, set body.contentType to "html" and put an actual HTML element
        <at id="n">escaped visible label</at> in body.content. Each element's numeric n must equal
        the corresponding mentions entry's numeric id. mentionText must match the visible label
        after HTML entity decoding; HTML-escape label text, not the <at> element itself.
        For the current user-mention task use mentioned.user for a resolved PERSON:
        id, displayName and userIdentityType "aadUser". This chooses the mention target,
        not the message sender; keep the configured authenticated principal unchanged.
        Generic person example of the decoded jsonBody object (placeholders are not real identities):
        {"body":{"contentType":"html","content":"Hi <at id=\"0\">Example Person</at>"},"mentions":[{"id":0,"mentionText":"Example Person","mentioned":{"user":{"id":"RESOLVED_USER_ID","displayName":"Example Person","userIdentityType":"aadUser"}}}]}
        This minimal example needs no extra @odata.type annotations. The client forwards your selected
        native parentUrl/jsonBody unchanged; it does not construct or repair the message for you.
        Resolve placeholders from native results; do not send the examples or infer identities from labels.
        When jsonBody is a string argument, encode that JSON object as a string exactly once at the
        argument boundary. Parsing jsonBody must yield an object, not another serialized JSON string.
        Parsing body.content must yield HTML with actual quote characters in <at id="0">, not literal
        backslash-quote characters. JSON transport escapes are necessary; double-escaped HTML is not.
        A successful user mention does not establish native team-tag support.
        When the user requires a native Teams tag notification, plain @text, a channel mention,
        or expansion into individual user mentions is not an equivalent result.
        Establish the requested team/channel and unique team-scoped tag from native results, not names alone.
        A mention needs a real target identity and matching HTML <at id> / mention index; a null target is not a tag mention.
        Confirm that the available native contract supports the tag identity; do not invent missing schema support.
        If unsupported or unresolved, report the gap rather than silently posting without the required tag.
        Message creation acceptance, rendered tag mention and delivered notifications are separate observations.
        Never retry an uncertain post or switch endpoint/version to force support.
        """;
    private static readonly HashSet<string> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        "agentId", "agent_id", "headers", "authorization", "accessToken", "token", "apiKey", "api_key",
        "endpoint", "baseUrl", "callbackUrl", "webhookUrl"
    };
    private static readonly HashSet<string> DiagnosticKeys = new(StringComparer.Ordinal)
    {
        "entityUrls", "entityUrl", "parentUrl", "actionUrl", "path", "jsonBody", "operationType", "format", "filter", "query"
    };
    internal static DirectDuplicateField? FindDuplicate(JsonElement args)
    {
        DirectDuplicateField? Visit(JsonElement value, int depth, string source)
        {
            if (depth > 32) throw new LabException("Direct MCP nested argument depth exceeded.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                var duplicate = value.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal)
                    .FirstOrDefault(group => group.Skip(1).Any());
                if (duplicate is not null)
                {
                    bool reserved = Overrides.Contains(duplicate.Key);
                    string key = reserved ? Overrides.Single(k => k.Equals(duplicate.Key, StringComparison.OrdinalIgnoreCase)) :
                        DiagnosticKeys.Contains(duplicate.Key) ? duplicate.Key : "withheld";
                    string first = duplicate.First().Value.GetRawText();
                    return new(key, reserved ? "ReservedOverride" : key == "withheld" ? "Withheld" : "NativeArgument",
                        duplicate.Count(), depth, source, duplicate.All(p => p.Value.GetRawText() == first));
                }
                foreach (JsonProperty p in value.EnumerateObject())
                {
                    DirectDuplicateField? found;
                    if (p.Name == "jsonBody" && p.Value.ValueKind == JsonValueKind.String)
                    {
                        using JsonDocument body = JsonDocument.Parse(p.Value.GetString()!, new JsonDocumentOptions { MaxDepth = 16 });
                        found = Visit(body.RootElement, depth + 1, "JsonBody");
                    }
                    else found = Visit(p.Value, depth + 1, source);
                    if (found is not null) return found;
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (JsonElement child in value.EnumerateArray())
                    if (Visit(child, depth + 1, source) is { } found) return found;
            return null;
        }
        return Visit(args, 0, "ModelArguments");
    }
    internal static bool Excluded(WorkIqTool tool) =>
        !Regex.IsMatch(tool.Name, @"\A[A-Za-z0-9_.-]{1,64}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
        tool.Name == "tools/list" ||
        Regex.IsMatch(tool.Name, @"(?:^|[._-])ask|delegat|orchestrat|authenticate|consent|execute.code|shell",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)) ||
        Regex.IsMatch(tool.Description ?? "", @"\b(delegate|delegates|delegating|autonomous|orchestrates)\b",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    internal static WorkIqTool ForModel(WorkIqTool raw)
    {
        if (Excluded(raw)) throw new LabException("Direct MCP tool unavailable: opaque delegation, credential workflow or unsupported tool name.");
        if (raw.InputSchema.ValueKind == JsonValueKind.Object &&
            raw.InputSchema.TryGetProperty("required", out JsonElement requiredKeys) && requiredKeys.ValueKind == JsonValueKind.Array &&
            requiredKeys.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && Overrides.Contains(v.GetString()!)))
            throw new LabException($"Direct MCP required identity/transport override unavailable; tool [{raw.Name}].");
        if (raw.InputSchema.ValueKind != JsonValueKind.Object)
            throw new LabException("Native MCP inputSchema must be an object; no model request.");
        void References(JsonElement value, int depth)
        {
            if (depth > 32) throw new LabException("Native MCP schema exceeds the JSON depth safety bound.");
            if (value.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty p in value.EnumerateObject())
                {
                    if (p.Name is "$ref" or "$dynamicRef" or "$recursiveRef" &&
                        (p.Value.ValueKind != JsonValueKind.String || !p.Value.GetString()!.StartsWith('#')))
                        throw new LabException("External/non-fragment schema reference unavailable; no external schema retrieval.");
                    References(p.Value, depth + 1);
                }
            else if (value.ValueKind == JsonValueKind.Array) foreach (JsonElement child in value.EnumerateArray()) References(child, depth + 1);
        }
        References(raw.InputSchema, 0);
        // The SDK accepts BinaryData for native non-strict schemas. Do not translate its schema dialect,
        // close open objects, drop maps/constraints/annotations, or run the guarded-mode type validator.
        if (!raw.InputSchema.TryGetProperty("properties", out JsonElement props) || props.ValueKind != JsonValueKind.Object ||
            !props.EnumerateObject().Any(p => Overrides.Contains(p.Name))) return raw;
        JsonObject schema = JsonNode.Parse(raw.InputSchema.GetRawText())!.AsObject();
        JsonObject properties = schema["properties"]!.AsObject();
        foreach (string key in properties.Select(p => p.Key).Where(Overrides.Contains).ToArray()) properties.Remove(key);
        return raw with { InputSchema = JsonSerializer.SerializeToElement(schema) };
    }

    internal static void Validate(WorkIqTool raw, JsonElement args)
    {
        if (Excluded(raw)) throw new LabException("Unavailable direct tool; no dispatch.");
        if (args.ValueKind != JsonValueKind.Object)
            throw new LabException("MCP tool arguments must be a JSON object; no dispatch.");
        if (FindDuplicate(args) is { } duplicate)
            throw new LabException("Direct MCP duplicate argument/body fields denied; ambiguous arguments rejected. " + duplicate.Diagnostic);
        if (args.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty p in args.EnumerateObject())
                if (Overrides.Contains(p.Name))
                {
                    string key = Overrides.Single(k => k.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                    throw new LabException($"Direct MCP argument override denied; tool [{raw.Name}]; key [{key}]; kind [{p.Value.ValueKind}]. Values withheld; no dispatch.");
                }
        // Nested grant headers/agent routing must not smuggle transport control through a data object.
        void Check(JsonElement value, int depth = 0)
        {
            if (depth > 32) throw new LabException("Direct MCP nested argument depth exceeded.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in value.EnumerateObject())
                {
                    if (Overrides.Contains(p.Name))
                        throw new LabException("Direct MCP nested identity/transport override denied; values withheld.");
                    if (p.Name == "jsonBody" && p.Value.ValueKind == JsonValueKind.String)
                    {
                        using JsonDocument body = JsonDocument.Parse(p.Value.GetString()!, new JsonDocumentOptions { MaxDepth = 16 });
                        Check(body.RootElement, depth + 1);
                    }
                    Check(p.Value, depth + 1);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (JsonElement child in value.EnumerateArray()) Check(child, depth + 1);
        }
        Check(args);
    }

    internal static bool MayMutate(WorkIqTool tool, JsonElement args)
    {
        // Failure semantics only: this classification never vetoes a fresh model-selected call.
        if (tool.ReadOnlyHint == false || tool.DestructiveHint == true) return true;
        string[] readArguments = tool.Name switch
        {
            "fetch" => ["entityUrls"], "fetch_blob" => ["path", "format"],
            "get_schema" => ["path", "operationType", "format"],
            // query is the configured descriptor; filter is the observed native discovery variant.
            "search_paths" => ["query", "filter"], "list_agents" => [], _ => []
        };
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(p => !readArguments.Contains(p.Name))) return true;
        if (tool.Name is "get_schema" or "search_paths" or "list_agents") return false;
        if (tool.Name is not ("fetch" or "fetch_blob")) return true;
        if (tool.Name == "fetch_blob" && args.TryGetProperty("format", out JsonElement format) && format.ValueKind != JsonValueKind.Null)
            return true;
        // This is effect classification, not a resource allowlist. Unknown GET function/share-redemption
        // semantics use mutation failure handling; paths outside the invoking channel pass to WorkIQ.
        bool Ambiguous(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => Risky(value.GetString()!),
            JsonValueKind.Array => value.EnumerateArray().Any(Ambiguous),
            JsonValueKind.Object => value.EnumerateObject().Any(p => Ambiguous(p.Value)),
            _ => false
        };
        return Ambiguous(args);
    }
    private static bool Risky(string value)
    {
        string decoded = value;
        for (int n = 0; n < 4; n++)
        {
            string next = Uri.UnescapeDataString(decoded);
            if (next == decoded) break;
            decoded = next;
            if (n == 3) return true;
        }
        return decoded.Contains("/shares/", StringComparison.OrdinalIgnoreCase) ||
            decoded.Contains('(') || decoded.Contains(')') ||
            decoded.Contains("redeem", StringComparison.OrdinalIgnoreCase) ||
            decoded.Contains("grant", StringComparison.OrdinalIgnoreCase) ||
            decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    }
}
