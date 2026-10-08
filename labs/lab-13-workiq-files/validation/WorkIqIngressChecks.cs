using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    internal static async Task WorkIqIngressChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (string command in new[] { "/file read drive file", "/file rename drive file next.txt",
            "/file move drive file folder", "/file share-with-chat drive file", "/folder create new",
            "/upload-folder create", "/channel messages", "/team info", "/thread replies link",
            "/message-workiq root", "/team-graph channels", "/setup file", "/setup channel",
            "/setup upload", "/setup sharing", "/confirm old-token", "/cancel old-token",
            "/mcp-confirm old-token", "/mcp-cancel old-token", "/unknown",
            "<p>/file rename drive file next.txt</p>", "  /confirm old-token  ", "<b>&#47;folder create new</b>" })
            await check("Retired WorkIQ slash ingress never dispatches: " + command, async () =>
            {
                await using PrivateHarness p = new();
                var activity = Harness.Activity(command);
                PrivateIngress privateResult = await p.Coordinator.Handle(activity, default);
                string output = (await App(p.H, p.Model, p.ModelOptions).Handle(activity, default))!;
                Must(!privateResult.Handled && output == WorkIqIngress.Retired &&
                    p.Model.Requests.Count == 0 && p.Routing.Requests.Count == 0 &&
                    p.H.Handler.CatalogCalls == 0 && p.H.Handler.ToolCalls == 0 && p.Store.List().Length == 0);
            });
        foreach (string command in new[] { "/help", "help", "/status", "status" })
            await check("Operational WorkIQ control uses no data/model calls: " + command, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                ModelHandler model = new();
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(command), default))!;
                Must(output.Contains(command.Contains("help") ? "LLM-native" : "DirectMcp") &&
                    !output.Contains("/file rename") && model.Requests.Count == 0 &&
                    h.Handler.ToolCalls == 0 && h.Handler.CatalogCalls == 0);
            });
        foreach (string key in new[] { "SetupMode:Enabled", "ChannelSetup:Enabled",
            "FilePolicy:Contexts:0:AutoExecuteFolderCreateTestMode", "FilePolicy:Contexts:0:AutoExecuteRenameTestMode",
            "FilePolicy:Contexts:0:AutoExecuteMoveTestMode", "FilePolicy:Contexts:0:AutoExecuteGroupShareTestMode",
            "FilePolicy:Contexts:0:SharingRosterDiscoveryOnly", "FilePolicy:Contexts:0:UploadMappingDiagnosticEnabled",
            "FilePolicy:Contexts:0:SectionTwoEnabled", "FilePolicy:Contexts:0:GroupShareEnabled",
            "FilePolicy:Contexts:0:AutoChannelFolderCreateEnabled", "FilePolicy:Contexts:0:AutoChannelRenameEnabled",
            "FilePolicy:Contexts:0:AutoChannelMoveEnabled", "FilePolicy:Contexts:0:AutoChannelArtifactCreateEnabled",
            "FilePolicy:Contexts:0:AutoChannelContentEditEnabled" })
            await check("Retired WorkIQ configuration ignores saved activation: " + key, () =>
            {
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { [key] = "true" }).Build();
                FilePolicy policy = configuration.GetSection("FilePolicy").Get<FilePolicy>() ?? new();
                string warning = RetiredWorkIqConfiguration.Apply(configuration, policy);
                Must(warning.Contains(key.Replace(":0:", ":*:")) && warning.Contains("effective values are false") &&
                    configuration[key] == "true" && RetiredWorkIqConfiguration.Apply(new ConfigurationBuilder().Build(), policy) == "");
                return Task.CompletedTask;
            });
        await check("WorkIQ production assembly contains no legacy execution engine", () =>
        {
            foreach (string type in new[] { "FileAgent", "FileService", "WorkIqData", "ScopedNames", "ChannelRoots",
                "GroupSharing", "SharingRoster", "UploadSetup", "NaturalLanguageBroker", "NaturalLanguageLoop", "NaturalToolContract" })
                Must(typeof(WorkIqIngress).Assembly.GetType("WorkIqFiles." + type) is null, type);
            Must(typeof(FilePolicy).Assembly.GetType("WorkIqFiles.Confirmations") is null);
            return Task.CompletedTask;
        });
        await WorkIqNativeEntryChecks(check);
        await DirectPrivateErrorChecks(check);
        await WorkIqStartupChecks(check);
    }
}
