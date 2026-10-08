using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace WorkIqFiles;

// Comparisons stay observational; optional source callbacks serve the explicit handle adapter without dispatching.
internal sealed class DirectContinuationDiagnostic
{
    private const int MaxSources = 64, MaxRows = 48, MaxUrlChars = 32768;
    private static readonly string[] QueryKeys = ["$top", "$select", "$filter", "$orderby"];
    private sealed record Address(string Raw, string Resource, Dictionary<string, string> Query);
    private sealed class Source(int call, int? resultItem, string provenance, Address address, string fragment)
    {
        internal readonly int Call = call;
        internal readonly int? ResultItem = resultItem;
        internal readonly string Provenance = provenance;
        internal readonly Address Address = address;
        internal readonly string Fragment = fragment;
        internal string Presentation = "not-assessed";
    }
    private readonly List<Source> sources = [];
    private readonly List<string> rows = [];
    private bool sourceLimit, rowLimit;

    internal string Receipt(int maxChars)
    {
        if (rows.Count == 0 && sources.Count == 0) return "";
        string header = "\nContinuation diagnostic (dispatch-boundary observation, not proof of HTTP send; JSON-decoded strings; token NOT percent-decoded; no raw values):\n";
        List<string> details = rows.Concat(sources.Select(s =>
            $"source call={s.Call}/resultItem={Index(s.ResultItem)}; provenance={s.Provenance}; sourceLinkPresent=true; " +
            $"{Fingerprint("sourceUrl", s.Address.Raw)}; {TokenFingerprint("sourceToken", s.Address)}; presentation={s.Presentation}")).ToList();
        // Keep complete newest comparison rows first; never truncate a hash or silently omit evidence.
        List<string> selected = [];
        int chars = header.Length + 200;
        foreach (string detail in rows.AsEnumerable().Reverse().Concat(details.Skip(rows.Count).Reverse()))
            if (chars + detail.Length + 1 <= maxChars) { selected.Add(detail); chars += detail.Length + 1; }
        return header + string.Join("\n", selected) +
            $"\nreceiptRowsOmitted={details.Count - selected.Count}; sourceLimit={Bool(sourceLimit)}; comparisonRowLimit={Bool(rowLimit)}." +
            (sourceLimit ? " Source coverage unavailable; comparisons are not conclusive." : "");
    }

    private static string Index(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable";
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Fingerprint(string label, string value) =>
        $"{label}Chars={value.Length}; {label}Utf8Bytes={Encoding.UTF8.GetByteCount(value)}; " +
        $"{label}Sha256={WorkIqSkill.Hash(value)}";
    private static string? Token(Address address) => address.Query.GetValueOrDefault("$skiptoken");
    private static string TokenFingerprint(string label, Address address) => Token(address) is { } value
        ? Fingerprint(label, value) : label + "=absent";
    private void Add(string row)
    {
        if (rows.Count == MaxRows) { rows.RemoveAt(0); rowLimit = true; }
        rows.Add(row);
    }

    private static Address? Parse(string raw)
    {
        if (raw.Length is 0 or > MaxUrlChars || raw.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            raw.IndexOfAny(['\\', '#']) >= 0) return null;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '%' && (i + 2 >= raw.Length || !Uri.IsHexDigit(raw[i + 1]) || !Uri.IsHexDigit(raw[i + 2])))
                return null;
            if (char.IsSurrogate(raw[i]))
            {
                if (!char.IsHighSurrogate(raw[i]) || i + 1 >= raw.Length || !char.IsLowSurrogate(raw[i + 1])) return null;
                i++;
            }
        }
        string relative = raw;
        const string origin = "https://graph.microsoft.com";
        if (raw.StartsWith(origin + "/", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) || uri.Host != "graph.microsoft.com" ||
                uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
            relative = raw[origin.Length..];
            if (relative.StartsWith("/v1.0/", StringComparison.Ordinal)) relative = relative[5..];
            else if (relative.StartsWith("/beta/", StringComparison.Ordinal)) return null; // Never equate beta and versionless v1 paths.
        }
        else if (!raw.StartsWith('/') || raw.StartsWith("//", StringComparison.Ordinal)) return null;
        int question = relative.IndexOf('?');
        string path = question < 0 ? relative : relative[..question];
        if (path.Contains("/../", StringComparison.Ordinal) || path.Contains("/./", StringComparison.Ordinal)) return null;
        Dictionary<string, string> query = new(StringComparer.Ordinal);
        if (question >= 0)
            foreach (string pair in relative[(question + 1)..].Split('&'))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0) return null;
                string key = pair[..equals];
                // Decode parameter names only. Values (particularly opaque tokens) remain exact.
                key = Uri.UnescapeDataString(key);
                if (!query.TryAdd(key, pair[(equals + 1)..])) return null;
            }
        return new(raw, path, query);
    }

    internal void Dispatch(int call, string tool, JsonElement args)
    {
        if (tool != "fetch" || !args.TryGetProperty("entityUrls", out JsonElement urls) || urls.ValueKind != JsonValueKind.Array) return;
        int entity = 0;
        foreach (JsonElement value in urls.EnumerateArray())
        {
            entity++;
            string prefix = $"outgoing call={call}/entity={entity}; ";
            if (value.ValueKind != JsonValueKind.String || Parse(value.GetString()!) is not { } outgoing)
            {
                Add(prefix + "comparison=unavailable-invalid-or-unsupported-url; sourceLinkPresent=not-assessed");
                continue;
            }
            Source[] relevant = sources.Where(s => s.Address.Resource == outgoing.Resource).ToArray();
            Source[] exact = relevant.Where(s => s.Address.Raw == outgoing.Raw).ToArray();
            Source[] normalized = relevant.Where(s => Equivalent(s.Address, outgoing)).ToArray();
            Source[] sameToken = Token(outgoing) is { } token
                ? relevant.Where(s => Token(s.Address) == token).ToArray() : [];
            Source[] sameQuery = relevant.Where(s => SameOtherQuery(s.Address, outgoing)).ToArray();
            if (Token(outgoing) is null && exact.Length == 0 && normalized.Length == 0)
            {
                Add(prefix + $"sourceLinkPresent={(relevant.Length > 0 ? "true" : "not-observed-for-resource")}; comparison=not-a-token-continuation; " +
                    Fingerprint("outgoingUrl", outgoing.Raw));
                continue;
            }
            Source[] candidates = exact.Length > 0 ? exact : normalized.Length > 0 ? normalized :
                sameToken.Length > 0 ? sameToken : sameQuery.Length > 0 ? sameQuery : relevant;
            string outgoingFacts = Fingerprint("outgoingUrl", outgoing.Raw) + "; " + TokenFingerprint("outgoingToken", outgoing);
            if (candidates.Length != 1 || sourceLimit)
            {
                Add(prefix + $"sourceLinkPresent={(relevant.Length > 0 ? "true" : "not-observed-for-resource")}; " +
                    $"comparison={(sourceLimit ? "unavailable-source-limit" : candidates.Length == 0 ? "unavailable-no-source" : "ambiguous")}; " +
                    $"candidateCount={candidates.Length}; " + outgoingFacts);
                continue;
            }
            Source source = candidates[0];
            Address expected = source.Address;
            string queryChanges = string.Join(",", QueryKeys.Select(key =>
                key + ":" + (!expected.Query.ContainsKey(key) && !outgoing.Query.ContainsKey(key) ? "absent" :
                    !expected.Query.ContainsKey(key) ? "added" : !outgoing.Query.ContainsKey(key) ? "removed" :
                    expected.Query[key] == outgoing.Query[key] ? "same" : "changed")));
            Add(prefix + $"sourceCall={source.Call}; sourceResultItem={Index(source.ResultItem)}; provenance={source.Provenance}; sourceLinkPresent=true; " +
                $"exactUrlMatch={Bool(expected.Raw == outgoing.Raw)}; sameResource=true; " +
                $"knownOriginEquivalent={Bool(expected.Raw != outgoing.Raw && Equivalent(expected, outgoing))}; " +
                $"rawTokenMatch={(Token(expected) is { } a && Token(outgoing) is { } b ? Bool(a == b) : "unavailable-missing-token")}; " +
                "tokenEquality=ordinal-codepoints-and-utf8-for-valid-unicode; " +
                $"query={queryChanges}; otherQueryChanged={Bool(!SameUnknownQuery(expected, outgoing))}; " +
                $"sourcePresentation={source.Presentation}; {outgoingFacts}; {Fingerprint("sourceUrl", expected.Raw)}; {TokenFingerprint("sourceToken", expected)}");
        }
    }
    private static bool Equivalent(Address a, Address b) =>
        a.Resource == b.Resource && Relative(a) == Relative(b);
    private static string Relative(Address address)
    {
        int query = address.Raw.IndexOf('?');
        return address.Resource + (query < 0 ? "" : address.Raw[query..]);
    }
    private static bool SameOtherQuery(Address a, Address b) =>
        a.Query.Where(p => p.Key != "$skiptoken").OrderBy(p => p.Key, StringComparer.Ordinal)
            .SequenceEqual(b.Query.Where(p => p.Key != "$skiptoken").OrderBy(p => p.Key, StringComparer.Ordinal));
    private static bool SameUnknownQuery(Address a, Address b) =>
        a.Query.Where(p => p.Key != "$skiptoken" && !QueryKeys.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal)
            .SequenceEqual(b.Query.Where(p => p.Key != "$skiptoken" && !QueryKeys.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal));

    internal void Observe(int call, string tool, JsonElement args, CallToolResult result,
        Action<string, int?, string, string?>? pageLink = null, Action? unavailableLink = null)
    {
        if (tool != "fetch") return;
        int count = args.TryGetProperty("entityUrls", out var urls) && urls.ValueKind == JsonValueKind.Array ? urls.GetArrayLength() : 0;
        int before = sources.Count;
        bool issue = false;
        if (result.IsError == true) { unavailableLink?.Invoke(); Add($"source call={call}; sourceLinkPresent=not-assessed; observation=unavailable-mcp-error"); return; }
        void Unavailable(string category) { issue = true; unavailableLink?.Invoke(); Add($"source call={call}; sourceLinkPresent=not-assessed; observation={category}"); }
        string[] requests = urls.ValueKind == JsonValueKind.Array
            ? urls.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];
        int nodes = 0;
        void Read(JsonElement node, string path, int? resultItem, string fragment, int depth, string? attributed = null)
        {
            if (++nodes > 512) { Unavailable("unavailable-node-limit"); return; }
            if (depth > 8) { Unavailable("unavailable-wrapper-depth"); return; }
            if (node.ValueKind == JsonValueKind.String)
            {
                string text = node.GetString()!;
                try { using JsonDocument nested = JsonDocument.Parse(text); Read(nested.RootElement, path + "/json", resultItem, fragment, depth + 1, attributed); }
                catch (JsonException) { Unavailable("unavailable-non-json-payload"); }
                return;
            }
            if (node.ValueKind != JsonValueKind.Object) { Unavailable("unavailable-payload-shape"); return; }
            if (node.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1))
            { Unavailable("unavailable-duplicate-properties"); return; }
            if (node.TryGetProperty("error", out _) ||
                node.TryGetProperty("statusCode", out var status) &&
                (status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out int code) || code < 200 || code >= 300))
            { Unavailable("unavailable-resource-error"); return; }
            if (pageLink is not null && (node.TryGetProperty("data", out _) || node.TryGetProperty("structuredContent", out _)))
            {
                string[] echoes = new[] { "entityUrl", "url" }.Where(k => node.TryGetProperty(k, out _))
                    .Select(k => node.GetProperty(k).ValueKind == JsonValueKind.String ? node.GetProperty(k).GetString()! : "").ToArray();
                if (echoes.Length > 0)
                {
                    string? matched = requests.Where(r => echoes.All(e => e == r)).Distinct(StringComparer.Ordinal).SingleOrDefault();
                    if (matched is null || attributed is not null && attributed != matched) unavailableLink?.Invoke();
                    attributed = matched;
                }
            }
            // Only collection-page siblings are eligible. Never enter value items, message bodies or arbitrary properties.
            if (node.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (string key in new[] { "@odata.nextLink", "nextLink" })
                    if (node.TryGetProperty(key, out var link))
                    {
                        pageLink?.Invoke(link.ValueKind == JsonValueKind.String ? link.GetString()! : "", resultItem, path + "/" + key, attributed);
                        if (link.ValueKind != JsonValueKind.String || Parse(link.GetString()!) is not { } address)
                        { Unavailable("unavailable-invalid-or-unsupported-link"); continue; }
                        if (resultItem is null) { Unavailable("unavailable-result-association"); continue; }
                        if (sources.Any(s => s.Call == call && s.ResultItem == resultItem && s.Address.Raw == address.Raw)) continue;
                        if (sources.Count >= MaxSources) { sourceLimit = true; continue; }
                        sources.Add(new(call, resultItem, path + "/" + key, address, fragment));
                    }
                return;
            }
            if (node.TryGetProperty("@odata.nextLink", out _) || node.TryGetProperty("nextLink", out _))
            { Unavailable("unavailable-non-collection-link"); return; }
            foreach (string wrapper in new[] { "structuredContent", "data" })
                if (node.TryGetProperty(wrapper, out var child)) Read(child, path + "/" + wrapper, resultItem, fragment, depth + 1, attributed);
            if (node.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement child in results.EnumerateArray())
                    Read(child, path + "/results[" + ++index + "]", index, fragment, depth + 1,
                        results.GetArrayLength() == 1 ? attributed : null);
            }
        }
        if (result.StructuredContent is { } structured)
        {
            if (structured.ValueKind == JsonValueKind.Undefined) Unavailable("unavailable-undefined-structured-content");
            else Read(structured, "structuredContent", count == 1 ? 1 : null, structured.GetRawText(), 0, requests.Length == 1 ? requests[0] : null);
        }
        int block = 0;
        foreach (ContentBlock content in result.Content ?? [])
        {
            block++;
            if (content is TextContentBlock text)
            {
                if (text.Text is null) { Unavailable("unavailable-null-text-content"); continue; }
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(text.Text);
                    Read(doc.RootElement, "content[" + block + "]/json", count == 1 ? 1 : null, text.Text, 0, requests.Length == 1 ? requests[0] : null);
                }
                catch (JsonException) { Unavailable("unavailable-non-json-content"); }
            }
        }
        if (before == sources.Count && !issue)
            Add($"source call={call}; sourceLinkPresent=false; observation=no-eligible-page-link");
    }

    internal void Presented(int call, string result)
    {
        foreach (Source source in sources.Where(s => s.Call == call))
            source.Presentation = result.Contains(source.Fragment, StringComparison.Ordinal)
                ? "preserved-original-json-fragment-in-queued-model-message" : "not-assessed-fragment-not-preserved";
    }
}
