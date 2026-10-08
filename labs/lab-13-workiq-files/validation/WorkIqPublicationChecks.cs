using WorkIqFiles;

internal static class WorkIqPublicationChecks
{
    private static void Must(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    internal static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("Published WorkIQ has its own entrypoint and required common dependency only", () =>
        {
            var host = typeof(WorkIqSession).Assembly;
            Must(host.EntryPoint is not null, "Missing runnable entrypoint.");
            string[] references = host.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
            Must(references.Contains("AgenticLabs.Common"), "Missing shared library.");
            Must(!references.Any(n => n is "TeamsMcpLab" or "GraphSdkLab" or "Microsoft.Graph"),
                "Foreign data provider dependency.");
            string[] forbidden = ["TeamsMcpSession", "GraphDataAgent", "GraphPrivateWorker", "HumanChatWorker",
                "GraphAgentUserTokenProvider", "SectionTwoService", "TeamsNativeContract"];
            Must(!host.GetTypes().Concat(typeof(BotHost).Assembly.GetTypes()).Any(t => forbidden.Contains(t.Name)),
                "Foreign provider implementation leaked into publication.");
            return Task.CompletedTask;
        });
        foreach (string key in new[] { "NaturalLanguage:DirectMcp:UseTeamsMcp", "TeamsMcp:HumanChat:Enabled",
            "TeamsMcp:PrivateGraph:Enabled", "TeamsMcp:GraphFiles:Enabled", "TeamsMcp:GraphChat:Enabled",
            "TeamsMcp:GraphChannel:Enabled", "GraphRscComparison:Enabled" })
            await check("Published WorkIQ rejects foreign provider activation " + key, () =>
            {
                IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { [key] = "true" }).Build();
                try { ProviderIsolation.Validate(config, LabProvider.WorkIq); }
                catch (InvalidOperationException error)
                {
                    Must(error.Message.Contains(key) && error.Message.Contains("No provider fallback"),
                        "Missing actionable provider isolation message.");
                    return Task.CompletedTask;
                }
                throw new Exception("Foreign provider accepted.");
            });
        await check("Published WorkIQ sample and embedded resources are self-contained", () =>
        {
            IConfiguration config = new ConfigurationBuilder().AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "appsettings.example.json")).Build();
            ProviderIsolation.Validate(config, LabProvider.WorkIq);
            var options = config.GetSection("NaturalLanguage").Get<NaturalLanguageOptions>()!;
            WorkIqHostPolicy policy = WorkIqHostPolicy.Read(config, options);
            Must(policy.Principal == "AgentUser" && !policy.AllowHumanIdentity, "Unsafe sample defaults.");
            string[] resources = typeof(WorkIqSkill).Assembly.GetManifestResourceNames();
            Must(resources.Length == 15 && resources.All(n => n.StartsWith("WorkIqFiles.workiq")),
                "Unexpected embedded guide set.");
            Must(typeof(BotHost).Assembly.GetManifestResourceNames().Length == 0, "Common embeds provider guidance.");
            foreach (WorkIqGuideMode mode in Enum.GetValues<WorkIqGuideMode>())
                Must(WorkIqSkill.For(mode).Status.Contains("D6E0CD296E241445BE0C041502F21FAB11434EB0E1A1546C22A075A39338E960") &&
                    WorkIqSkill.For(mode).Prompt.Length < 10000, "Guide integrity changed.");
            return Task.CompletedTask;
        });
        await check("Published common infrastructure owns no authentication session or private worker", () =>
        {
            string[] forbidden = ["WorkIqSession", "AgentUserTokenProvider", "HumanConnections", "PrivateAnalysisWorker"];
            Must(!typeof(BotHost).Assembly.GetTypes().Any(t => forbidden.Contains(t.Name)), "Provider state hidden in common.");
            return Task.CompletedTask;
        });
        await check("Published WorkIQ rejects Graph commands without dispatch", async () =>
        {
            await using Harness h = new();
            string? result = await h.App.Handle(Harness.Activity("/team-graph channels"), default);
            Must(result == WorkIqIngress.Retired && h.Handler.ToolCalls == 0 && h.Handler.AuthRequests == 0,
                "Foreign command dispatch or fallback.");
        });
    }
}
