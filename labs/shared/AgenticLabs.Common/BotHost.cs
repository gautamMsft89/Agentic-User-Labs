using Microsoft.AspNetCore.Builder;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Handlers;
using Microsoft.Teams.Apps.Schema;

namespace WorkIqFiles;

internal enum LabProvider { WorkIq, TeamsMcp, GraphSdk }

internal static class ProviderIsolation
{
    internal const string WorkIqOnly = "TeamsMcp provider selected in a WorkIQ-only host. Teams MCP moved to lab-14-teams-mcp; Graph SDK moved to lab-15-graph-sdk. No request or provider fallback.";
    internal static void Validate(IConfiguration config, LabProvider provider)
    {
        void Reject(string key, string destination)
        {
            if (config.GetValue<bool>(key))
                throw new InvalidOperationException($"{key}=true is unsupported in {provider}. This workflow moved to {destination}; disable this flag here and configure that separate project. No provider fallback.");
        }
        if (provider != LabProvider.TeamsMcp)
            Reject("NaturalLanguage:DirectMcp:UseTeamsMcp", "lab-14-teams-mcp");
        if (provider != LabProvider.WorkIq)
        {
            foreach (string key in new[] { "HumanWorkIQ:Enabled", "PrivateAnalysis:Enabled", "SetupMode:Enabled",
                "ChannelSetup:Enabled", "NaturalLanguage:FullWorkIqGuide" })
                Reject(key, "lab-13-workiq-files");
        }
        if (provider != LabProvider.GraphSdk)
            foreach (string key in new[] { "TeamsMcp:GraphFiles:Enabled", "TeamsMcp:GraphChat:Enabled",
                "TeamsMcp:GraphChannel:Enabled", "TeamsMcp:HumanChat:Enabled", "TeamsMcp:PrivateGraph:Enabled",
                "GraphRscComparison:Enabled" })
                Reject(key, "lab-15-graph-sdk");
    }
}

internal static class BotHost
{
    internal static WebApplicationBuilder Create(string[] args, LabProvider provider)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        ProviderIsolation.Validate(builder.Configuration, provider);
        builder.Services.AddTeamsBotApplication();
        builder.Services.AddHttpClient(ProgressDeliveryHandler.ClientName)
            .AddHttpMessageHandler(() => new ProgressDeliveryHandler(TimeProvider.System));
        builder.Services.AddSingleton(TimeProvider.System);
        foreach (string category in new[] { "Microsoft.Teams", "Microsoft.Identity", "System.Net.Http.HttpClient",
            "OpenAI", "Microsoft.AspNetCore.Authentication", "Microsoft.AspNetCore.Hosting.Diagnostics" })
            builder.Logging.AddFilter(category, LogLevel.None);
        return builder;
    }

    internal static TeamsBotApplication Messages(WebApplication app, LabSettings settings, FilePolicy policy,
        string provider, Func<MessageActivity, CancellationToken, IDirectProgress, Task<string?>> handle,
        Func<MessageActivity, Func<MessageActivity, CancellationToken, Task>, CancellationToken, Task<bool>>? ingress = null,
        Func<string, MessageActivity>? decorate = null)
    {
        TeamsBotApplication teams = app.UseTeamsBotApplication();
        teams.OnMessage(async (context, cancellationToken) =>
        {
            using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, app.Lifetime.ApplicationStopping);
            if (ingress is not null && await ingress(context.Activity,
                async (message, ct) => { await context.SendActivityAsync(message, ct); }, request.Token)) return;
            using DirectProgress progress = new(context.Activity, settings, policy,
                async (message, ct) => (await context.SendActivityAsync(message, ct))?.Id,
                async (id, message, ct) => { await context.Api.Conversations.Activities.UpdateAsync(
                    message.Conversation!.Id, id, message, cancellationToken: ct); },
                app.Lifetime.ApplicationStopping, app.Logger, providerName: provider);
            string? response = await progress.RunAsync(() => handle(context.Activity, request.Token, progress));
            if (response is null) return;
            await context.SendActivityAsync(decorate?.Invoke(response) ??
                new MessageActivity { Text = BotText.Html(response), TextFormat = TextFormats.Xml }, request.Token);
        });
        return teams;
    }
}
