using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Handlers;
using Microsoft.Teams.Apps.Schema;
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
    string? answer = await agent.Handle(request, timeout.Token);
    if (answer is not null) await context.SendAsync(new MessageActivity(answer) { TextFormat = TextFormats.Markdown }, ct);
});

await app.RunAsync();
return 0;

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
