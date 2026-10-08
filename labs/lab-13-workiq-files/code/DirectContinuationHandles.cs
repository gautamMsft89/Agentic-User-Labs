using System.Security.Cryptography;
using System.Text.Json;

namespace WorkIqFiles;

// Lifetime is one DirectRun, whose principal/context is revalidated before every dispatch.
internal sealed class DirectContinuationHandles
{
    internal const string ToolName = "fetch_next_page";
    internal static WorkIqTool Tool => new(ToolName, JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new { handle = new { type = "string" } },
            required = new[] { "handle" }, additionalProperties = false
        }),
        "App-owned adapter: choose an issued continuation handle to perform exactly one native WorkIQ fetch. " +
        "No automatic paging/retry. Uses stored service query unchanged except validated Graph v1.0 origin removal. " +
        "Unknown/used/cross-run handles stop. Prefer handles over rebuilding URLs; errors mean partial coverage.");
    private sealed record Entry(string Raw, string Relative, int Call, int? Item, string Provenance)
    {
        internal bool Used;
    }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly List<(string Raw, int? Item, string Provenance, string? Request)> candidates = [];
    private bool candidateLimit;
    private bool unavailable;
    private int retained;
    internal void Unavailable() => unavailable = true;
    internal void Capture(string raw, int? item, string provenance, string? request)
    {
        if (candidates.Count == 64) { candidateLimit = true; return; }
        candidates.Add((raw, item, provenance, request));
    }
    internal static string? Relative(string raw)
    {
        if (raw.Length is 0 or > 32768 || raw.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            raw.IndexOfAny(['\\', '#']) >= 0) return null;
        const string prefix = "https://graph.microsoft.com/v1.0/";
        string path = raw.StartsWith(prefix, StringComparison.Ordinal) ? "/" + raw[prefix.Length..] : raw;
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)) return null;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '%' && (i + 2 >= path.Length || !Uri.IsHexDigit(path[i + 1]) || !Uri.IsHexDigit(path[i + 2]))) return null;
            if (char.IsSurrogate(path[i]))
            {
                if (!char.IsHighSurrogate(path[i]) || i + 1 >= path.Length || !char.IsLowSurrogate(path[++i])) return null;
            }
        }
        string resource = path.Split('?', 2)[0];
        // Decoding is validation only; never dispatch the decoded/canonicalized value.
        string decoded = Uri.UnescapeDataString(resource);
        if (decoded.Contains('%') || decoded.IndexOfAny(['\\', '?', '#']) >= 0 ||
            decoded.Any(char.IsControl) || decoded[1..].Split('/').Any(s => s is "." or ".." or "")) return null;
        if (resource.StartsWith("/v1.0/", StringComparison.OrdinalIgnoreCase) ||
            resource.StartsWith("/beta/", StringComparison.OrdinalIgnoreCase)) return null;
        return path;
    }
    internal string Publish(int call)
    {
        if (candidates.Count == 0 && !candidateLimit && !unavailable) return "";
        List<object> issued = [];
        HashSet<string> reasons = new(StringComparer.Ordinal);
        int rejected = 0;
        foreach (var group in candidates.GroupBy(c => c.Request, StringComparer.Ordinal))
        {
            var values = group.DistinctBy(c => c.Raw).ToArray();
            var source = values[0];
            string? relative = Relative(source.Raw), request = source.Request is { } r ? Relative(r) : null;
            if (unavailable || candidateLimit || values.Length != 1 || source.Item is null || relative is null || request is null ||
                relative.Split('?', 2)[0] != request.Split('?', 2)[0] ||
                !relative.Contains('?') || relative == request ||
                entries.Values.Any(e => e.Relative == relative))
            {
                reasons.Add(unavailable ? "source-evidence-unavailable" : candidateLimit ? "candidate-limit" :
                    values.Length != 1 ? "conflicting-links" : source.Request is null || source.Item is null ? "unattributed-source" :
                    relative is null || request is null ? "unsafe-or-unsupported-url" :
                    relative.Split('?', 2)[0] != request.Split('?', 2)[0] ? "different-resource" :
                    entries.Values.Any(e => e.Relative == relative) ? "already-issued" : "not-new-continuation");
                rejected++; continue;
            }
            if (entries.Count >= 64 || retained + source.Raw.Length > 131072)
            { reasons.Add("registry-limit"); rejected++; continue; }
            string handle = "page_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            entries.Add(handle, new(source.Raw, relative, call, source.Item, source.Provenance));
            retained += source.Raw.Length;
            issued.Add(new { handle, sourceCall = call, sourceResultItem = source.Item,
                provenance = source.Provenance, urlSha256 = WorkIqSkill.Hash(source.Raw) });
        }
        candidates.Clear();
        bool limit = candidateLimit;
        candidateLimit = false;
        bool incomplete = unavailable;
        unavailable = false;
        return "\nApp-owned continuation handles (not provider data; optional one-page selections only):\n" +
            JsonSerializer.Serialize(new { tool = ToolName, handles = issued, unavailableCandidates = rejected, candidateLimit = limit,
                sourceEvidenceUnavailable = incomplete, unavailableReasons = reasons.Order(StringComparer.Ordinal).ToArray() });
    }
    internal (JsonElement Args, string Evidence) Resolve(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Count() != 1 ||
            !args.TryGetProperty("handle", out var field) || field.ValueKind != JsonValueKind.String ||
            !entries.TryGetValue(field.GetString()!, out var entry) || entry.Used)
            throw new LabException("Continuation handle unavailable, malformed, used or from another invocation; no native dispatch. Report partial coverage.");
        entry.Used = true;
        return (JsonSerializer.SerializeToElement(new { entityUrls = new[] { entry.Relative } }),
            $"App continuation adapter selection; sourceCall={entry.Call}; sourceResultItem={entry.Item}; " +
            $"sourceProvenance={entry.Provenance}; " +
            $"sourceUrlSha256={WorkIqSkill.Hash(entry.Raw)}; originOnlyMapping={entry.Raw != entry.Relative}; one native fetch, no automatic replay.");
    }
}
