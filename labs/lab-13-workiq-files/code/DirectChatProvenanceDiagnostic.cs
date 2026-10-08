using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace WorkIqFiles;

// Observes protocol data only; never provides arguments or decisions to execution.
internal sealed class DirectChatProvenanceDiagnostic
{
    private const int MaxSources = 64, MaxMembers = 128, MaxNodes = 512, MaxPayloadChars = 262144;
    private sealed record Resource(string Url, string Kind, string? Id, bool Plain);
    private sealed class Source(int call, int? item, string provenance, string association,
        string kind, string id, string chatType, string[]? members, int count, string completeness, string? fragment)
    {
        internal readonly int Call = call;
        internal readonly int? Item = item;
        internal readonly string Provenance = provenance, Association = association, Kind = kind, Id = id, ChatType = chatType;
        internal readonly string[]? Members = members;
        internal readonly int Count = count;
        internal readonly string Completeness = completeness;
        internal readonly string? Fragment = fragment;
        internal string Presentation = "not-assessed";
        internal string RosterKey => JsonSerializer.Serialize(new { users = (Members ?? []).Order(StringComparer.Ordinal), Count, Completeness });
    }
    private readonly List<Source> sources = [];
    private readonly List<string> dispatches = [];
    private readonly HashSet<string> unavailable = new(StringComparer.Ordinal);
    private bool limited;
    private int retainedChars, omittedDispatches;

    private void Unavailable(string category) => unavailable.Add(category);
    private static string? String(JsonElement node, string key) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static bool Id(string? id) => id is { Length: > 0 and <= 1024 } && !id.Any(char.IsControl);
    private static string? Relative(string raw)
    {
        if (raw.Length is 0 or > 8192 || raw.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            raw.IndexOfAny(['\\', '#']) >= 0) return null;
        for (int i = 0; i < raw.Length; i++)
            if (raw[i] == '%' && (i + 2 >= raw.Length || !Uri.IsHexDigit(raw[i + 1]) || !Uri.IsHexDigit(raw[i + 2]))) return null;
        const string origin = "https://graph.microsoft.com/v1.0";
        if (raw.StartsWith(origin + "/", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Host != "graph.microsoft.com" ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
            raw = raw[origin.Length..];
        }
        return raw.StartsWith('/') && !raw.StartsWith("//", StringComparison.Ordinal) ? raw : null;
    }
    private static Resource? Parse(string raw, bool send = false)
    {
        string? relative = Relative(raw);
        if (relative is null) return null;
        int query = relative.IndexOf('?');
        string path = query < 0 ? relative : relative[..query];
        string[] parts = path.Split('/').Skip(1).ToArray();
        if (parts.Length == 1 && parts[0] == "me" && !send) return new(relative, "self", null, query < 0);
        if (parts.Length > 1 && parts[0] == "me") parts = parts[1..];
        if (parts.Length == 0 || parts[0] != "chats") return null;
        if (parts.Length == 1 && !send) return new(relative, "list", null, query < 0);
        if (parts.Length is < 2 or > 3) return null;
        string id = Uri.UnescapeDataString(parts[1]);
        if (!Id(id) || id.IndexOfAny(['/', '\\', '?', '#']) >= 0 || id is "." or "..") return null;
        if (send) return parts.Length == 3 && parts[2] == "messages" && query < 0 ? new(relative, "send", id, true) : null;
        return parts.Length == 2 ? new(relative, "chat", id, query < 0) :
            parts[2] == "members" ? new(relative, "members", id, query < 0) : null;
    }
    private static bool Partial(JsonElement node, string prefix = "") =>
        new[] { prefix + "@odata.nextLink", prefix + "nextLink" }
            .Any(key => node.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null) ||
        new[] { prefix + "incomplete", prefix + "truncated" }
            .Any(key => node.TryGetProperty(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False));

    internal void Observe(int call, string tool, JsonElement args, CallToolResult result)
    {
        if (tool != "fetch" || args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("entityUrls", out var urls) || urls.ValueKind != JsonValueKind.Array) return;
        string[] requests = urls.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!).ToArray();
        if (!requests.Any(url => Parse(url) is not null)) return;
        if (result.IsError == true) { Unavailable("mcp-error"); return; }
        int nodes = 0, payloadChars = 0;
        bool Step() { if (++nodes <= MaxNodes) return true; limited = true; Unavailable("node-limit"); return false; }
        void Save(Source source)
        {
            if (sources.Any(s => s.Call == source.Call && s.Item == source.Item && s.Kind == source.Kind &&
                s.Id == source.Id && s.ChatType == source.ChatType && s.RosterKey == source.RosterKey)) return;
            if (sources.Count == MaxSources) { limited = true; Unavailable("source-limit"); return; }
            sources.Add(source);
        }
        (string[]? Users, int Count, string Complete) Roster(JsonElement owner, bool expanded, bool plain)
        {
            string key = expanded ? "members" : "value";
            if (!owner.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
                return (null, -1, "not-established");
            if (array.GetArrayLength() > MaxMembers) { limited = true; Unavailable("member-limit"); return (null, -1, "unavailable-limit"); }
            List<string> users = [];
            bool valid = true;
            foreach (JsonElement member in array.EnumerateArray())
            {
                if (!Step()) return (null, -1, "unavailable-limit");
                if (member.ValueKind != JsonValueKind.Object ||
                    member.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1) ||
                    String(member, "@odata.type") != "#microsoft.graph.aadUserConversationMember" ||
                    !Id(String(member, "userId"))) { valid = false; continue; }
                users.Add(String(member, "userId")!);
            }
            if (users.Distinct(StringComparer.Ordinal).Count() != users.Count) valid = false;
            bool partial = Partial(owner) || expanded && Partial(owner, "members");
            string countKey = expanded ? "members@odata.count" : "@odata.count";
            bool hasCount = owner.TryGetProperty(countKey, out var total);
            bool countMatches = hasCount && total.ValueKind == JsonValueKind.Number &&
                total.TryGetInt32(out int number) && number == array.GetArrayLength();
            string complete = !valid ? "unavailable-typed-identities" : partial || hasCount && !countMatches ? "partial-or-inconsistent" :
                expanded ? (countMatches ? "source-count-matches" : "not-established-expansion") :
                plain ? "plain-members-no-continuation" : "not-established-projected-members";
            return (users.ToArray(), array.GetArrayLength(), complete);
        }
        void Data(JsonElement data, Resource resource, int? item, string path, string association, string? fragment)
        {
            if (!Step()) return;
            if (data.ValueKind != JsonValueKind.Object ||
                data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1))
            { Unavailable("resource-shape-or-duplicates"); return; }
            if (data.TryGetProperty("error", out _) || data.TryGetProperty("statusCode", out var status) &&
                (status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out int code) || code is < 200 or >= 300))
            { Unavailable("resource-error"); return; }
            if (resource.Kind == "list")
            {
                if (!data.TryGetProperty("value", out var array) || array.ValueKind != JsonValueKind.Array)
                { Unavailable("chat-list-shape"); return; }
                foreach (JsonElement chat in array.EnumerateArray())
                {
                    if (!Step()) break;
                    Data(chat, resource with { Kind = "chat", Id = null }, item, path + "/value", association, fragment);
                }
                return;
            }
            if (resource.Kind == "members")
            {
                var roster = Roster(data, false, resource.Plain);
                Save(new(call, item, path, association, "members", resource.Id!, "not-assessed", roster.Users,
                    roster.Count, roster.Complete, fragment));
                return;
            }
            string? id = String(data, "id");
            if (!Id(id)) { Unavailable("missing-resource-id"); return; }
            if (resource.Id is not null && resource.Id != id) { Unavailable("entity-id-mismatch"); return; }
            if (resource.Kind == "self")
            {
                Save(new(call, item, path, association, "self", id!, "not-assessed", null, -1, "not-assessed", fragment));
                return;
            }
            string type = String(data, "chatType") switch { "oneOnOne" => "oneOnOne", "group" => "group", "meeting" => "meeting", _ => "unavailable" };
            var members = Roster(data, true, false);
            Save(new(call, item, path, association, "chat", id!, type, members.Users, members.Count, members.Complete, fragment));
        }
        void Walk(JsonElement node, Resource? resource, int? item, string path, string association, string? fragment, int depth)
        {
            if (!Step()) return;
            if (depth > 8) { Unavailable("wrapper-depth"); return; }
            if (node.ValueKind == JsonValueKind.String)
            {
                string text = node.GetString()!;
                if (text.Length > MaxPayloadChars) { limited = true; Unavailable("payload-limit"); return; }
                try { using JsonDocument nested = JsonDocument.Parse(text, new() { MaxDepth = 32 }); Walk(nested.RootElement, resource, item, path + "/json", association, fragment, depth + 1); }
                catch (JsonException) { Unavailable("non-json-payload"); }
                return;
            }
            if (node.ValueKind != JsonValueKind.Object ||
                node.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1))
            { Unavailable("wrapper-shape-or-duplicates"); return; }
            if (node.TryGetProperty("error", out _) || node.TryGetProperty("statusCode", out var status) &&
                (status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out int code) || code is < 200 or >= 300))
            { Unavailable("resource-error"); return; }
            // Only protocol wrappers may establish request attribution, never arbitrary resource properties.
            bool wrapper = node.TryGetProperty("data", out _) || node.TryGetProperty("results", out _) ||
                node.TryGetProperty("structuredContent", out _);
            if (wrapper)
            {
                if (Partial(node)) { Unavailable("partial-protocol-wrapper"); return; }
                string[] echoes = new[] { String(node, "entityUrl"), String(node, "url") }.OfType<string>().ToArray();
                if (echoes.Length > 0)
                {
                    string[] matching = requests.Where(r => Relative(r) is { } relative &&
                        echoes.All(e => Relative(e) == relative)).Distinct(StringComparer.Ordinal).ToArray();
                    if (matching.Length != 1)
                    { Unavailable("ambiguous-request-attribution"); return; }
                    Resource? matched = Parse(matching[0]);
                    if (matched is null) return; // An attributable but unrelated resource is not chat evidence.
                    if (resource is not null && resource.Url != matched.Url)
                    { Unavailable("ambiguous-request-attribution"); return; }
                    resource = matched; association = "echoed-request";
                }
            }
            if (node.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement child in results.EnumerateArray())
                {
                    if (!Step()) break;
                    Walk(child, results.GetArrayLength() == 1 ? resource : null, ++index,
                        path + "/results[" + index + "]", results.GetArrayLength() == 1 ? association : "unavailable", fragment, depth + 1);
                }
                return;
            }
            foreach (string key in new[] { "structuredContent", "data" })
                if (node.TryGetProperty(key, out var value))
                { Walk(value, resource, item, path + "/" + key, association, fragment, depth + 1); return; }
            if (resource is null) { Unavailable("unattributed-result"); return; }
            Data(node, resource, item, path, association, fragment);
        }
        void Root(JsonElement root, string path, string raw)
        {
            payloadChars += raw.Length;
            if (payloadChars > MaxPayloadChars) { limited = true; Unavailable("payload-limit"); return; }
            string? fragment = null;
            if (retainedChars + raw.Length <= 131072) { fragment = raw; retainedChars += raw.Length; }
            else Unavailable("presentation-fragment-limit");
            Resource? sole = requests.Length == 1 ? Parse(requests[0]) : null;
            Walk(root, sole, requests.Length == 1 ? 1 : null, path, sole is null ? "unavailable" : "sole-request", fragment, 0);
        }
        if (result.StructuredContent is { ValueKind: not JsonValueKind.Undefined } structured)
            Root(structured, "structuredContent", structured.GetRawText());
        int block = 0;
        foreach (ContentBlock content in result.Content ?? [])
        {
            block++;
            if (!Step()) break;
            if (content is not TextContentBlock { Text: { } text }) { Unavailable("non-text-content"); continue; }
            if (text.Length > MaxPayloadChars) { limited = true; Unavailable("payload-limit"); continue; }
            try { using JsonDocument doc = JsonDocument.Parse(text, new() { MaxDepth = 32 }); Root(doc.RootElement, "content[" + block + "]/json", text); }
            catch (JsonException) { Unavailable("non-json-content"); }
        }
    }

    internal void Presented(int call, string text)
    {
        foreach (Source source in sources.Where(s => s.Call == call))
            source.Presentation = source.Fragment is { } fragment && text.Contains(fragment, StringComparison.Ordinal)
                ? "preserved-in-queued-model-message" : "not-assessed-fragment-unavailable";
    }

    internal void Dispatch(int call, string tool, JsonElement args)
    {
        if (tool != "create_entity" || String(args, "parentUrl") is not { } parent) return;
        Resource? destination = Parse(parent, true);
        if (destination is null) return;
        string[] mentions = [];
        if (String(args, "jsonBody") is { } body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body, new() { MaxDepth = 16 });
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("mentions", out var array) && array.ValueKind == JsonValueKind.Array)
                    mentions = array.EnumerateArray().Take(9).Select(m =>
                        m.ValueKind == JsonValueKind.Object && m.TryGetProperty("mentioned", out var target) &&
                        target.ValueKind == JsonValueKind.Object && target.TryGetProperty("user", out var user)
                            ? String(user, "id") : null).Where(Id).Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
            }
            catch (JsonException) { Unavailable("malformed-outgoing-body"); }
        }
        Source[] chats = sources.Where(s => s.Kind == "chat" && s.Id == destination.Id).ToArray();
        Source[] rosters = sources.Where(s => s.Kind != "self" && s.Id == destination.Id && s.Members is not null).ToArray();
        string[] selves = sources.Where(s => s.Kind == "self").Select(s => s.Id).Distinct(StringComparer.Ordinal).ToArray();
        string[] types = chats.Select(s => s.ChatType).Where(t => t != "unavailable").Distinct(StringComparer.Ordinal).ToArray();
        bool conflict = rosters.Select(s => s.RosterKey).Distinct(StringComparer.Ordinal).Count() > 1;
        Source? roster = conflict ? null : rosters.FirstOrDefault();
        string Member(string[] identities) => conflict ? "ambiguous" : roster?.Members is null || identities.Length != 1
            ? "not-assessed" : roster.Members.Contains(identities[0], StringComparer.Ordinal) ? "true" : "false";
        bool complete = roster?.Completeness is "source-count-matches" or "plain-members-no-continuation";
        string exact = limited || conflict || types.Length != 1 || types[0] != "oneOnOne" || !complete ||
            selves.Length != 1 || mentions.Length != 1 ? "not-established" :
            roster!.Count == 2 && selves[0] != mentions[0] && Member(selves) == "true" && Member(mentions) == "true"
                ? "matches-caller-and-selected-mention" : "does-not-match";
        string observed = chats.Length > 0 ? "true" : limited || unavailable.Count > 0 ? "unavailable" : "not-observed-in-captured-eligible-data";
        string row = $"outgoing call={call}; targetIntent=not-assessed; selectedChatIdSha256={WorkIqSkill.Hash(destination.Id!)}; " +
            $"idComparison=ordinal-after-single-path-segment-unescape; selectedIdObserved={observed}; matchingChatObservations={chats.Length}; " +
            $"distinctChatIdsObserved={sources.Where(s => s.Kind == "chat").Select(s => s.Id).Distinct(StringComparer.Ordinal).Count()}; " +
            $"chatType={(types.Length == 1 ? types[0] : types.Length > 1 ? "ambiguous" : "not-assessed")}; " +
            $"rosterEvidence={(conflict ? "ambiguous-conflicting" : roster?.Completeness ?? "not-assessed")}; " +
            $"rosterCount={(roster is null ? "not-assessed" : roster.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))}; " +
            $"typedUserIdCount={(roster?.Members is null ? "not-assessed" : roster.Members.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))}; " +
            $"attributedMeIdentityCount={selves.Length}; selectedMentionIdentityCount={mentions.Length}; " +
            $"callerMemberObserved={Member(selves)}; selectedMentionMemberObserved={Member(mentions)}; exactTwoPersonRoster={exact}";
        Source[] relevant = chats.Concat(rosters).Concat(sources.Where(s => s.Kind == "self")).Distinct().ToArray();
        foreach (Source source in relevant.Take(6))
            row += $"\nsource call={source.Call}/resultItem={source.Item?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}; " +
                $"kind={source.Kind}; provenance={source.Provenance}; attribution={source.Association}; presentation={source.Presentation}";
        row += $"\nsourceRowsOmitted={Math.Max(0, relevant.Length - 6)}";
        if (dispatches.Count == 8) { dispatches.RemoveAt(0); omittedDispatches++; }
        dispatches.Add(row);
    }

    internal string Receipt(int maxChars)
    {
        if (dispatches.Count == 0) return "";
        const string header = "\nChat provenance diagnostic (observation only; not delivery/intent proof; queued does not mean model-attended; no raw IDs/URLs/rosters):\n";
        string status = "\nsourceLimit=" + limited + "; unavailable=" +
            (unavailable.Count == 0 ? "none" : string.Join(",", unavailable.Order(StringComparer.Ordinal))) + "; ";
        List<string> rows = [];
        int chars = header.Length + status.Length + 100;
        foreach (string row in dispatches.AsEnumerable().Reverse())
            if (chars + row.Length + 1 <= maxChars) { rows.Add(row); chars += row.Length + 1; }
        if (maxChars < header.Length + status.Length + 100) return "\nChat provenance diagnostic unavailable: receipt budget.";
        return header + string.Join("\n", rows) + status +
            $"dispatchRowsOmitted={omittedDispatches + dispatches.Count - rows.Count}.";
    }
}
