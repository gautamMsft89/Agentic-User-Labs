using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Handlers;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Apps.Schema.Entities;
using WorkIqNaturalLanguage;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
if (!LabSetup.Load(builder.Configuration, builder.Environment.ContentRootPath)) return 1;
foreach (string category in new[] { "Microsoft.Teams", "Microsoft.Identity", "System.Net.Http.HttpClient", "OpenAI",
    "Microsoft.AspNetCore.Authentication", "Microsoft.AspNetCore.Hosting.Diagnostics" })
    builder.Logging.AddFilter(category, LogLevel.None);

builder.Services.AddTeamsBotApplication();
AgentUserToken.Register(builder.Services, builder.Configuration);
builder.Services.AddSingleton(sp => new WorkIqConnection(new Uri(builder.Configuration["WorkIQ:Endpoint"]!),
    sp.GetRequiredService<AgentUserToken>().AuthorizationHeader));
builder.Services.AddSingleton(sp => new Agent(builder.Configuration, sp.GetRequiredService<WorkIqConnection>()));

WebApplication app = builder.Build();
TeamsBotApplication teams = app.UseTeamsBotApplication();
Agent agent = app.Services.GetRequiredService<Agent>();
teams.UseMiddleware(new ActivityLog()); // diagnostic: shows non-message activities (e.g. ambient notifications of another type)

// Load the WorkIQ tool list once at startup so setup problems show up before the first Teams message.
try
{
    var tools = await app.Services.GetRequiredService<WorkIqConnection>().Catalog(CancellationToken.None);
    Console.WriteLine($"WorkIQ connected as the agent user. Tools ({tools.Count}): {string.Join(", ", tools.Select(t => t.Name))}");
}
catch (Exception error)
{
    Console.WriteLine($"WorkIQ check failed: {error.Message}");
    Console.WriteLine("The host still starts; the next Teams message tries again. See README 'Troubleshooting'.");
}

teams.OnMessage(async (context, ct) =>
{
    Request? request = Agent.Admit(context.Activity, builder.Configuration);
    if (request is null) return; // not for us: wrong tenant, recipient or sender
    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, app.Lifetime.ApplicationStopping);
    timeout.CancelAfter(TimeSpan.FromMinutes(5));
    Progress? progress = request.IsPersonal && !request.Overheard ? new Progress(context) : null; // 1:1 only
    if (progress is not null) await progress.Status("Working on it…", ct);
    string? answer = await agent.Handle(request, timeout.Token, progress is null ? null : text => progress.Status(text, ct));
    if (progress is not null && await progress.Finish(answer, ct)) return;
    if (answer is not null) await context.SendAsync(new MessageActivity(answer) { TextFormat = TextFormats.Markdown }, ct);
});

await app.RunAsync();
return 0;

// 1:1 streaming while Otto works (README 'Streaming & progress'): status updates, then the answer streams in and is
// finalized as one message. Same payloads as the SDK's TeamsStreamingWriter (lab-03), but sent through
// context.SendAsync so each step's activity id is logged (the writer hides ids and silently drops chunks after
// Teams' 2-minute stream limit). Any failure logs "[stream] failed" and the caller sends one normal reply instead.
sealed class Progress(Context<MessageActivity> context)
{
    private static readonly TimeSpan StreamLimit = TimeSpan.FromSeconds(110); // Teams ends a stream after 2 minutes
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private string? streamId, last;
    private int sequence;
    private bool failed;
    private string T => $"t={clock.Elapsed.TotalSeconds:0.0}s";

    internal async Task Status(string text, CancellationToken ct)
    {
        if (failed || text == last) return;
        last = text;
        try
        {
            if (sequence == 0) Console.WriteLine("[stream] start");
            if (clock.Elapsed > StreamLimit) return; // too late to keep this stream alive; the answer is sent normally
            string? id = (await context.SendAsync(Step(text, "informative"), ct))?.Id;
            streamId ??= id;
            Console.WriteLine($"[stream] status \"{text}\" #{sequence} {T} id={id ?? "(none)"}");
        }
        catch (Exception error) { failed = true; Console.WriteLine($"[stream] failed: {Describe(error)}"); }
    }

    // Returns true when the answer was delivered here; false means the caller sends it normally.
    internal async Task<bool> Finish(string? answer, CancellationToken ct)
    {
        try
        {
            if (failed || streamId is null) { Console.WriteLine("[stream] not confirmed (no stream id) → normal reply"); return false; }
            if (clock.Elapsed > StreamLimit) { Console.WriteLine($"[stream] past the 2-minute stream limit ({T}) → normal reply"); return false; }
            string text = answer ?? "Done - see above."; // NO_REPLY (e.g. a card was posted): close the stream with a short line
            int chunks = Math.Clamp(text.Length / 80, 1, 12), size = (text.Length + chunks - 1) / chunks;
            for (int end = size; end < text.Length && clock.Elapsed < StreamLimit; end += size)
            {
                await Task.Delay(600, ct); // Teams throttles stream updates; the SDK writer also waits >= 500 ms
                string? id = (await context.SendAsync(Step(text[..end], "streaming"), ct))?.Id;
                Console.WriteLine($"[stream] chunk #{sequence} chars={end} {T} id={id ?? "(none)"}");
            }
            TeamsActivity final = TeamsActivity.CreateBuilder(new MessageActivity(text) { TextFormat = TextFormats.Markdown })
                .AddEntity(new StreamInfoEntity { StreamType = "final", StreamId = streamId }).Build();
            string? finalId = (await context.SendAsync(final, ct))?.Id;
            Console.WriteLine($"[stream] final chars={text.Length} {T} id={finalId ?? "(none)"}");
            return true;
        }
        catch (Exception error) { Console.WriteLine($"[stream] failed: {Describe(error)} → normal reply"); return false; }
    }

    // One stream step: a typing activity with a streaminfo entity (type, sequence, and the id of the first step).
    private StreamingActivity Step(string text, string type)
    {
        StreamingActivity step = new(text);
        step.StreamInfo.StreamType = type;
        step.StreamInfo.StreamSequence = ++sequence;
        if (streamId is not null) step.StreamInfo.StreamId = streamId;
        return step;
    }

    private static string Describe(Exception e) => e is HttpRequestException h ? $"http {(int?)h.StatusCode} {h.Message}" : e.Message;
}

sealed class ActivityLog : Microsoft.Teams.Core.ITurnMiddleware
{
    public Task OnTurnAsync(Microsoft.Teams.Core.BotApplication bot, Microsoft.Teams.Core.Schema.CoreActivity activity,
        Microsoft.Teams.Core.NextTurn next, CancellationToken ct)
    {
        if (activity.Type != "message")
            Console.WriteLine($"[in] {activity.Type} name={(activity.Properties?.TryGetValue("name", out object? n) == true ? n : "-")} conv={activity.Conversation?.Id}");
        return next(ct);
    }
}
