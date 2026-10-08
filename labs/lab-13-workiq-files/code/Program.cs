using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Handlers;
using Microsoft.Teams.Apps.Schema;
using System.Text.Json;
using WorkIqFiles;

WebApplicationBuilder builder = BotHost.Create(args, LabProvider.WorkIq);
LabSettings settings = new(builder.Configuration);
SetupMode setup = new();
FilePolicy policy = builder.Configuration.GetSection("FilePolicy").Get<FilePolicy>() ?? new();
string retiredWarning = RetiredWorkIqConfiguration.Apply(builder.Configuration, policy);
// Preserve retired rootless group audience records; native WorkIQ still refuses all group data access.
policy.Validate(allowInactiveGroupContexts: true);
ChannelSetup channelSetup = new();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(policy);
builder.Services.AddSingleton(setup);
builder.Services.AddSingleton(channelSetup);
NaturalLanguageOptions naturalOptions = builder.Configuration.GetSection("NaturalLanguage").Get<NaturalLanguageOptions>() ?? new();
WorkIqHostPolicy hostPolicy = WorkIqHostPolicy.Read(builder.Configuration, naturalOptions);
builder.Services.AddSingleton(hostPolicy);
AgentUserTokenProvider.Register(builder.Services, builder.Configuration);
HumanSettings? humanSettings = null;
string humanSetup = hostPolicy.AllowHumanIdentity ?
    "Human OAuth disabled/unconfigured. Configure HumanWorkIQ for matched-human operations; no identity fallback." :
    WorkIqHostPolicy.HumanUnavailable;
if (hostPolicy.HumanOAuthEnabled)
{
    try { humanSettings = new HumanSettings(builder.Configuration, settings); }
    catch (InvalidOperationException error) { humanSetup = "Human OAuth setup required: " + error.Message; }
}
if (humanSettings is not null) hostPolicy.RegisterHuman(builder.Services, builder.Configuration, humanSettings);
builder.Services.AddSingleton(sp => new WorkIqRouter(sp.GetRequiredService<WorkIqSession>(),
    sp.GetService<HumanConnections>(), humanSettings, settings, humanSetup, hostPolicy));
builder.Services.AddSingleton<WorkIqIngress>();
builder.Services.AddSingleton(naturalOptions);
builder.Services.AddSingleton<NaturalLanguageAgent>();
builder.Services.AddSingleton<IConversationAgent>(sp => sp.GetRequiredService<NaturalLanguageAgent>());
PrivateAnalysisOptions privateOptions = builder.Configuration.GetSection("PrivateAnalysis").Get<PrivateAnalysisOptions>() ?? new();
privateOptions.Enabled = hostPolicy.PrivateJobsEnabled;
builder.Services.AddSingleton(privateOptions);
if (privateOptions.Enabled)
{
    if (humanSettings is null || setup.Enabled || channelSetup.Enabled ||
        !builder.Configuration.GetValue<bool>("NaturalLanguage:Enabled") ||
        !builder.Configuration.GetValue<bool>("NaturalLanguage:DirectMcp:Enabled"))
        throw new InvalidOperationException("PrivateAnalysis requires configured HumanWorkIQ, enabled DirectMcp and completed setup. No configuration is changed automatically.");
    hostPolicy.RegisterPrivate(builder.Services, privateOptions, settings);
}
WebApplication app = builder.Build();
app.Logger.LogWarning("Effective WorkIQ startup: {State}", hostPolicy.Status);
if (retiredWarning.Length != 0) app.Logger.LogWarning("{RetiredWorkIqFlags}", retiredWarning);
if (!hostPolicy.AllowHumanIdentity &&
    (builder.Configuration.GetValue<bool>("HumanWorkIQ:Enabled") || builder.Configuration.GetValue<bool>("PrivateAnalysis:Enabled")))
    app.Logger.LogWarning("Saved human/private activation flags are inactive without WorkIQ:AllowHumanIdentity=true. No human callbacks or private workers registered.");
if (humanSettings is not null) BrowserSignIn.UseCallbackCleanup(app);
app.UseAuthentication();
app.UseAuthorization();
if (humanSettings is not null) hostPolicy.MapHuman(app);
else app.Logger.LogWarning("{HumanSetup}", humanSetup);
TeamsBotApplication teams = BotHost.Messages(app, settings, policy, "WorkIQ",
    (activity, ct, progress) => app.Services.GetRequiredService<WorkIqIngress>().Handle(activity, ct, progress),
    async (activity, send, ct) =>
{
    if (privateOptions.Enabled)
    {
        using PrivateIngressDiagnostic diagnostic = new();
        try
        {
            PrivateIngress result = await app.Services.GetRequiredService<PrivateAnalysisCoordinator>().Handle(activity, ct);
            if (result.Handled)
            {
                if (result.Reply is { } reply)
                {
                    PrivateIngressDiagnostic.Enter(PrivateIngressStage.ChannelReply);
                    await send(new MessageActivity { Text = BotText.Html(reply), TextFormat = TextFormats.Xml }, ct);
                }
                return true;
            }
        }
        catch (Exception error) when (PrivateFailures.SafeFailure(error))
        {
            app.Logger.LogWarning("Private routing stopped; category={Category}; details withheld.", PrivateFailures.Category(error));
            diagnostic.Log(app.Logger, error, routeFallback: false);
            try { PrivateContext.RequireSender(activity, settings); }
            catch (LabException) { app.Logger.LogWarning("Private response withheld: AU sender/reference unavailable; no app-only fallback."); return true; }
            await send(new MessageActivity
            { Text = "Private analysis could not continue. Open the approved personal chat and use /private-jobs. No identity fallback or private result is posted here." }, ct);
            return true;
        }
    }
    return false;
}, response =>
{
    MessageActivity outgoing = new() { Text = BotText.Html(response), TextFormat = TextFormats.Xml };
    string? signInUrl = humanSettings is null ? null : BrowserSignIn.PromptUrl(response, humanSettings);
    if (signInUrl is not null)
    {
        JsonElement card = JsonSerializer.SerializeToElement(new
        {
            type = "AdaptiveCard", version = "1.5",
            body = new[] { new { type = "TextBlock", text = "Sign in as your Teams human account. This private link expires in five minutes.", wrap = true } },
            actions = new[] { new { type = "Action.OpenUrl", title = "Sign in to WorkIQ", url = signInUrl } }
        });
        outgoing = new MessageActivity { Text = "Private human WorkIQ sign-in; no file operation runs on completion." }
            .AddAttachment(TeamsAttachment.CreateBuilder().WithAdaptiveCard(card).Build());
    }
    return outgoing;
});
if (privateOptions.Enabled)
    PrivateCardInvoke.Register(teams, () => app.Services.GetRequiredService<PrivateAnalysisCoordinator>(),
        app.Lifetime.ApplicationStopping, app.Logger);
await app.RunAsync();