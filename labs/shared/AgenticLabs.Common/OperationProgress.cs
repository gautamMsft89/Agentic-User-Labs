using System.Diagnostics;

namespace WorkIqFiles;

// Invocation-local diagnostics: fixed labels and counters only, never arguments or payloads.
internal sealed class OperationProgress : IDisposable
{
    private static readonly AsyncLocal<OperationProgress?> slot = new();
    private readonly OperationProgress? previous = slot.Value;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    internal static OperationProgress? Current => slot.Value;
    internal ReadCallTrace? ReadTrace;
    internal ReadCallTrace? SectionOneCalls;
    internal ReadCallTrace? DiscoveryCalls;
    internal int? NaturalLanguageCallBudget;
    internal NaturalLanguageBudgets? NaturalLanguageBudgets;
    internal WorkIqCallEvidence? DirectCallEvidence;
    internal string ReadPhase = "read preparation";
    internal string ReadTraceSummary => ReadTrace?.Render(elapsed.Elapsed.TotalMilliseconds) ?? "";
    internal string Phase = "authorization/read or preview";
    internal string Stage = "before backend";
    internal string Tool = "none";
    internal string CancellationSource = "unavailable (no cancellation source observed)";
    internal bool MutationMayHaveDispatched;
    internal int CallsStarted, CallsCompleted, AuthorityLookups;
    private readonly Dictionary<string, int> phaseCalls = new(StringComparer.Ordinal);
    internal void CallStarted()
    {
        CallsStarted++;
        phaseCalls[Phase] = phaseCalls.GetValueOrDefault(Phase) + 1;
    }
    internal string TimingSummary => $"elapsedMs={elapsed.Elapsed.TotalMilliseconds:F0}; " +
        $"backendCallsStarted={CallsStarted}; backendCallsCompleted={CallsCompleted}; " +
        "callsByPhase=[" + string.Join("; ", phaseCalls.Select(p => $"{p.Key}={p.Value}")) + "]. " +
        "Includes initial binding, authority and queues; excludes later Teams reply delivery.";
    internal double? ExecutionBudgetMs;
    internal OperationProgress() => slot.Value = this;
    internal string Diagnostic => $"phase={Phase}; stage={Stage}; tool={Tool}; " +
        $"dispatch={(MutationMayHaveDispatched ? "possibly dispatched (tools/call entered; not proof of HTTP delivery)" : "not dispatched by this invocation")}; " +
        $"elapsedMs={elapsed.Elapsed.TotalMilliseconds:F0}; backendCallsStarted={CallsStarted}; backendCallsCompleted={CallsCompleted}; " +
        $"freshAuthorityLookups={AuthorityLookups}; executionBudgetAtEntryMs={ExecutionBudgetMs?.ToString("F0") ?? "not entered"}; " +
        $"cancellationSource={CancellationSource}; " +
        "callsByPhase=[" + string.Join("; ", phaseCalls.Select(p => $"{p.Key}={p.Value}")) + "]";
    public void Dispose() => slot.Value = previous;
}
