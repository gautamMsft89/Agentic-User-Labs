using System.Text.Json.Nodes;
using System.Net;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private sealed class TimingClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        internal void Advance(long milliseconds) => ticks += milliseconds;
    }
    private static async Task TimingChecks(Func<string, Func<Task>, Task> check)
    {
        await check("NL aggregate fetch/blob discovery init queue auth do not double count", () =>
        {
            TimingClock clock = new();
            NaturalLanguageTiming timing = new(clock);
            ReadCallTrace execution = new(), discovery = new();
            void Add(ReadCallTrace trace, string tool, double ms, double init, double queue, double auth, string mode)
            {
                var call = trace.Start(tool, [], "read preparation");
                call.ToolMs = ms; call.Init = init; call.Queue = queue; call.Auth = auth;
                call.Mode = mode; call.Session = "fixture-g1"; trace.Finish(call);
            }
            Add(discovery, "tools/list", 10, 100, 7, 60, "new");
            Add(discovery, "get_schema", 20, 0, 5, 3, "reused");
            for (int i = 0; i < 20; i++) Add(execution, "fetch", 2, 0, 1, 1, "reused");
            Add(execution, "fetch_blob", 30, 0, 1, 10, "reused");
            clock.Advance(250);
            string rendered = timing.Render(execution, discovery, timing.TotalMs);
            Must(rendered.Contains("WorkIQ fetch: 40.0 ms (20 calls)") &&
                rendered.Contains("fetch_blob: 30.0 ms (1 calls)") &&
                rendered.Contains("MCP discovery: 30.0 ms (2 calls") &&
                rendered.Contains("MCP initialization: 100.0 ms") &&
                rendered.Contains("Total processing time: 250.0 ms") &&
                rendered.Contains("reads fixture-g1 (reused); discovery fixture-g1 (new)") &&
                !rendered.Contains("60.0 ms"), rendered);
            Must(execution.QueueMs == 21 && discovery.QueueMs == 12);
            return Task.CompletedTask;
        });
    }
}
