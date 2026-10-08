using System.Globalization;
using OpenAI.Chat;

namespace WorkIqFiles;

internal sealed class NaturalLanguageTiming(TimeProvider? provider = null)
{
    private readonly TimeProvider clock = provider ?? TimeProvider.System;
    private readonly long started = (provider ?? TimeProvider.System).GetTimestamp();
    internal double SelectionMs { get; private set; }
    internal double AnswerMs { get; private set; }
    internal double OtherModelMs { get; private set; }
    internal int SelectionCalls { get; private set; }
    internal int AnswerCalls { get; private set; }
    internal int OtherCalls { get; private set; }
    internal double ModelMs => SelectionMs + AnswerMs + OtherModelMs;
    internal long StartModel() => clock.GetTimestamp();
    internal void EndModel(long start, ChatCompletion? completion)
    {
        double ms = clock.GetElapsedTime(start).TotalMilliseconds;
        if (completion is not null && string.IsNullOrEmpty(completion.Refusal) &&
            completion.FinishReason == ChatFinishReason.ToolCalls && completion.ToolCalls.Count > 0)
        { SelectionMs += ms; SelectionCalls++; }
        else if (completion is not null && string.IsNullOrEmpty(completion.Refusal) &&
            completion.FinishReason == ChatFinishReason.Stop && completion.ToolCalls.Count == 0 &&
            completion.Content.Any(c => !string.IsNullOrWhiteSpace(c.Text)))
        { AnswerMs += ms; AnswerCalls++; }
        else { OtherModelMs += ms; OtherCalls++; }
    }
    internal double TotalMs => clock.GetElapsedTime(started).TotalMilliseconds;
    private static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
    internal string Render(ReadCallTrace execution, ReadCallTrace discovery, double total)
    {
        var fetch = execution.ForTool("fetch");
        var blob = execution.ForTool("fetch_blob");
        return $"\nTotal processing time: {F(total)} ms (excludes Teams delivery)\n" +
            $"LLM tool-selection: {F(SelectionMs)} ms ({SelectionCalls} model calls) | Final-answer generation: {F(AnswerMs)} ms ({AnswerCalls} model calls)" +
            (OtherCalls == 0 ? "" : $" | Failed/unclassified model: {F(OtherModelMs)} ms ({OtherCalls} calls)") +
            $"\nWorkIQ fetch: {F(fetch.Ms)} ms ({fetch.Count} calls)" +
            (blob.Count == 0 ? "" : $" | fetch_blob: {F(blob.Ms)} ms ({blob.Count} calls)") +
            $" | MCP discovery: {F(discovery.ToolMs)} ms ({discovery.Count} calls; " +
            $"tools/list {discovery.ForTool("tools/list").Count}, get_schema {discovery.ForTool("get_schema").Count}, search_paths {discovery.ForTool("search_paths").Count})" +
            $"\nMCP initialization: {F(execution.InitializationMs + discovery.InitializationMs)} ms | " +
            $"MCP session: reads {execution.SessionSummary}; discovery {discovery.SessionSummary}\n" +
            "LLM times are model-call durations, not internal reasoning. Tool times exclude initialization/queue; auth is nested.";
    }
}
