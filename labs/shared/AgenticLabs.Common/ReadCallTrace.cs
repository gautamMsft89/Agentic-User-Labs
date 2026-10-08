using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace WorkIqFiles;

// Invocation-local fixed labels only: never retain arguments, responses or identifiers.
internal sealed class ReadCallTrace
{
    internal const int Limit = 16;
    private readonly List<Call> calls = [];
    private int count;
    private int completed, http, tokens, reconnects;
    private double wall;
    private double queue, init, toolMs, auth;
    private string session = "none", aggregateMode = "not acquired";
    private readonly Dictionary<string, int> toolCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> toolTimes = new(StringComparer.Ordinal);
    internal int Count => count;
    internal double ToolMs => toolMs;
    internal double InitializationMs => init;
    internal double QueueMs => queue;
    internal string SessionSummary => $"{session} ({aggregateMode})";
    internal (int Count, double Ms) ForTool(string tool) =>
        (toolCounts.GetValueOrDefault(tool), toolTimes.GetValueOrDefault(tool));
    internal void Finish(Call call)
    {
        call.Total = call.Clock.Elapsed.TotalMilliseconds;
        completed++;
        http += call.Http; tokens += call.Tokens; reconnects += call.Reconnects; wall += call.Total;
        queue += call.Queue; init += call.Init; toolMs += call.ToolMs; auth += call.Auth;
        toolCounts[call.Tool] = toolCounts.GetValueOrDefault(call.Tool) + 1;
        toolTimes[call.Tool] = toolTimes.GetValueOrDefault(call.Tool) + call.ToolMs;
        if (call.Mode == "reconnected") aggregateMode = "reconnected";
        else if (aggregateMode != "reconnected")
        {
            if (call.Session != "none" && session != "none" && session != call.Session) aggregateMode = "changed";
            else if (aggregateMode != "changed" && (call.Mode == "new" || aggregateMode == "not acquired"))
                aggregateMode = call.Mode;
        }
        if (call.Session != "none") session = call.Session;
    }
    internal Call Start(string tool, Dictionary<string, object?> args, string phase)
    {
        Call call = new(++count, tool is "fetch" or "fetch_blob" or "create_entity" or "update_entity" or "do_action" or
            "tools/list" or "get_schema" or "search_paths" or "list_agents" ? tool : "other", Route(args), phase switch
        {
            "name discovery" or "name listing" or "target refresh" or "final authorization" or "requester drive" or
            "content transfer" or "post-content authorization" or "primary read" or "root validation" or "reply read" => phase,
            _ => "read preparation"
        });
        if (calls.Count < Limit) calls.Add(call);
        return call;
    }
    private static string Route(Dictionary<string, object?> args)
    {
        if (args.TryGetValue("entityUrls", out object? paths) && paths is string[] urls)
        {
            if (urls.Length != 1) return "metadata batch";
            string[] segments = urls[0].Split('?')[0].Split('/');
            if (segments.Length == 4 && segments[1] == "users" && segments[3] == "drive") return "/users/{user}/drive";
            if (segments.Length >= 5 && segments[1] == "drives" && segments[3] == "items")
                return segments.Length == 6 && segments[5] == "children" ? "/drives/{drive}/items/{item}/children" :
                    segments.Length == 5 ? "/drives/{drive}/items/{item}" : "scoped collection";
        }
        return args.ContainsKey("path") ? "item content" : "other";
    }
    internal string Render(double elapsed) =>
        $"\nRead trace: whole={F(elapsed)}ms; logical={count}; finished={completed}; shown={calls.Count}; omitted={count - calls.Count}; " +
        $"all-call-wall={F(wall)}ms; HTTP={http}; tok={tokens}; reconnect={reconnects}. Excludes Teams delivery.\n" +
        "ms: wall/queue/init/tool/auth(nested); HTTP=dispatch attempts; tok=token attempts. Auth overlaps init/tool, not additive. Logical includes queue; reconnect stays within one call. returned=reply received, not application validation success.\n" +
        string.Join("\n", calls.Select(c => $"{c.Number}. {c.Phase}; {c.Tool} {c.Route}; {c.Outcome}; {c.Mode}; " +
            $"{F(c.Total)}/{F(c.Queue)}/{F(c.Init)}/{F(c.ToolMs)}/{F(c.Auth)}; HTTP={c.Http}; tok={c.Tokens}; reconnect={c.Reconnects}"));
    private static string F(double n) => n.ToString("F1", CultureInfo.InvariantCulture);
    internal string WorkIqSummary() =>
        $"WorkIQ calls: {count} | Tool time: {toolMs.ToString("N1", CultureInfo.InvariantCulture)} ms (excludes MCP initialization) | " +
        $"MCP session: {session} ({aggregateMode})" +
        (toolCounts.Count > 1 ? " | " + string.Join(", ", toolCounts.Select(p => $"{p.Key}: {p.Value}")) : "");
    internal string RenderSectionTwo() =>
        $"WorkIQ fetch: logical={count}; finished={completed}; toolsCallMs={F(toolMs)}; " +
        $"queueMs={F(queue)}; initMs={F(init)}; authNestedMs={F(auth)}; sessionCallWallMs={F(wall)}; " +
        $"HTTP={http}; tokenAttempts={tokens}; reconnects={reconnects}.\n" +
        "toolsCallMs measures awaited MCP tools/call (including nested auth/transport/service and read-reconnect cleanup), " +
        "not wire-only or server processing; those timings are unavailable. Auth overlaps init/tools, not additive. " +
        "Partial failed/cancelled intervals included; returned means reply received, not validated data.\n" +
        string.Join("\n", calls.Select(c => $"{c.Number}. {c.Phase}; fetch; {c.Outcome}; {c.Mode}; " +
            $"toolsCallMs={F(c.ToolMs)}; queueMs={F(c.Queue)}; initMs={F(c.Init)}; authNestedMs={F(c.Auth)}; " +
            $"sessionCallWallMs={F(c.Total)}; HTTP={c.Http}; tokenAttempts={c.Tokens}; reconnects={c.Reconnects}"));
    internal sealed class Call(int number, string tool, string route, string phase)
    {
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly int Number = number;
        internal readonly string Tool = tool, Route = route, Phase = phase;
        internal string Outcome = "error", Mode = "not acquired";
        internal string Session = "none";
        internal double Total, Queue, Init, ToolMs, Auth;
        internal int Http, Tokens, Reconnects;
        internal static string Result(CallToolResult result)
        {
            if (result.IsError == true) return "tool-error";
            // Report a resource result, not successful authorization or parsing.
            if (result.StructuredContent is JsonElement data && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in results.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("statusCode", out JsonElement status) &&
                            status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int code) &&
                            code is < 200 or >= 300) return "resource-error";
                }
            }
            return "returned";
        }
    }
}
