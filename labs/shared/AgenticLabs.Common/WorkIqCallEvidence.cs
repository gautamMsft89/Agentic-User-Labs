using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace WorkIqFiles;

// Fixed labels/counts only. Transport callbacks retain evidence even when the SDK wraps or drops an exception.
internal sealed class WorkIqResponseEvidence
{
    internal string Stage = "no response observed", Failures = "none", ContentBlocks = "not decoded";
    internal string Media = "unavailable";
    internal int? Http, Rpc;
    internal long? DeclaredBytes, ReadBytes, ExceededBytes;
    internal bool Complete, ExceededAtLeast;
    internal int LimitBytes;
    internal void Reset(int limit)
    {
        Stage = "no response observed"; Failures = "none"; ContentBlocks = "not decoded";
        Media = "unavailable";
        Http = Rpc = null; DeclaredBytes = ReadBytes = ExceededBytes = null;
        Complete = ExceededAtLeast = false; LimitBytes = limit;
    }
    internal void Read(long bytes, bool complete)
    {
        ReadBytes = bytes; Complete = complete;
        Stage = complete ? "response body fully read; SDK processing" : "response body read";
    }
    internal void Failure(Exception error)
    {
        List<string> kinds = Failures == "none" ? [] : Failures.Split(',').ToList();
        for (int depth = 0; depth < 8 && error is not null; depth++, error = error.InnerException!)
        {
            if (error is HttpRequestException { StatusCode: { } status }) Http ??= (int)status;
            string kind = error switch
            {
                WorkIqEnvelopeLimitException limit => CaptureLimit(limit),
                McpProtocolException protocol => CaptureRpc(protocol),
                McpException => "McpException", HttpRequestException => "HttpRequestException",
                JsonException => "JsonException", OperationCanceledException => "OperationCanceledException",
                IOException => "IOException", FormatException => "FormatException",
                InvalidOperationException => "InvalidOperationException", ArgumentException => "ArgumentException",
                NotSupportedException => "NotSupportedException", _ => "other"
            };
            if (!kinds.Contains(kind)) kinds.Add(kind);
        }
        Failures = string.Join(",", kinds);
    }
    private string CaptureLimit(WorkIqEnvelopeLimitException limit)
    {
        ExceededBytes = limit.ObservedBytes; ExceededAtLeast = limit.AtLeast; LimitBytes = limit.LimitBytes;
        return "WorkIqEnvelopeLimitException";
    }
    private string CaptureRpc(McpProtocolException protocol)
    { Rpc = (int)protocol.ErrorCode; return "McpProtocolException"; }
    internal void Result(CallToolResult result)
    {
        if (result.Content is null) { ContentBlocks = "invalid-null"; return; }
        int text = 0, image = 0, other = 0;
        foreach (ContentBlock? block in result.Content)
        {
            if (block is TextContentBlock) text++;
            else if (block is ImageContentBlock) image++;
            else other++;
        }
        ContentBlocks = $"text={text},image={image},other={other}";
    }
    internal WorkIqCallEvidence Snapshot(BackendTiming timing, bool entered, int httpAttempts) =>
        new(timing, entered, httpAttempts, Http, Rpc, Stage, Failures, ContentBlocks,
            DeclaredBytes, ReadBytes, Complete, ExceededBytes, ExceededAtLeast, LimitBytes, Media);
}

internal sealed record WorkIqCallEvidence(BackendTiming Timing, bool ToolEntered, int HttpAttempts,
    int? Http, int? Rpc, string Stage, string Failures, string ContentBlocks,
    long? DeclaredBytes, long? ReadBytes, bool Complete, long? ExceededBytes, bool ExceededAtLeast, int LimitBytes, string Media)
{
    internal string Diagnostic =>
        $"MCP evidence: exceptionKinds=[{Failures}]; lastStage=[{Stage}]; outerHTTP=[{Http?.ToString() ?? "unavailable"}]; " +
        $"rpcCode=[{Rpc?.ToString() ?? "unavailable"}]; responseMedia=[{Media}]; HTTP attempts={HttpAttempts}; " +
        $"responseDeclaredBytes={DeclaredBytes?.ToString() ?? "unavailable"}; responseReadBytes={ReadBytes?.ToString() ?? "unavailable"}; " +
        $"responseComplete={Complete}; responseLimitBytes={LimitBytes}; contentBlocks=[{ContentBlocks}]; resourceStatus=unavailable." +
        (ExceededBytes is long bytes ? $" Response envelope limit exceeded: observed {(ExceededAtLeast ? ">=" : "")}{bytes} bytes, limit {LimitBytes} bytes; not the decoded-file size." : "");
}
