using System.Text.Json;
using ModelContextProtocol.Protocol;
namespace WorkIqFiles;

internal sealed record BackendTiming(string Session, string Mode, double QueueMs, double InitializationMs,
    double FetchMs, double TokenMs, int TokenCalls, double TotalMs);

internal sealed record ToolReply(CallToolResult Result, BackendTiming Timing, int? OuterHttpStatus = null);
internal sealed record WorkIqTool(string Name, JsonElement InputSchema, string? Description = null,
    bool? ReadOnlyHint = null, bool? DestructiveHint = null);
internal sealed record ToolCatalog(IReadOnlyList<WorkIqTool> Tools, BackendTiming Timing);
