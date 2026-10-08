using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private sealed class StartupEndpoints(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(services);
    }
    private static IConfiguration StartupConfig(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddConfiguration(Harness.Config)
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();
    private static WorkIqHostPolicy StartupPolicy(IConfiguration config) => WorkIqHostPolicy.Read(config,
        config.GetSection("NaturalLanguage").Get<NaturalLanguageOptions>() ?? new());
    private static async Task WorkIqStartupChecks(Func<string, Func<Task>, Task> check)
    {
        await check("WorkIQ absent principal and human opt-in preserve inactive subsystem defaults", () =>
        {
            var config = StartupConfig();
            var options = config.GetSection("NaturalLanguage").Get<NaturalLanguageOptions>() ?? new();
            var host = WorkIqHostPolicy.Read(config, options);
            Must(host.Principal == "AgentUser" && !host.PrincipalConfigured && !host.AllowHumanIdentity &&
                !host.HumanOAuthEnabled && !host.PrivateJobsEnabled && !options.Enabled &&
                !options.DirectMcp.Enabled && !options.FullWorkIqGuide);
            return Task.CompletedTask;
        });
        foreach (string principal in new[] { "SignedInHuman", "invalid", "", "agentuser" })
            await check("WorkIQ explicit conflicting or invalid principal never defaults to AU: " + principal, () =>
            {
                try { _ = StartupPolicy(StartupConfig(("NaturalLanguage:DirectMcp:Principal", principal))); }
                catch (InvalidOperationException error)
                {
                    Must(error.Message.Contains(principal == "SignedInHuman" ? "No identity fallback" : "Invalid"));
                    return Task.CompletedTask;
                }
                throw new Exception("Explicit principal silently defaulted.");
            });
        await check("WorkIQ standard configuration precedence respects explicit false and AU overrides", () =>
        {
            IConfiguration lower = StartupConfig((WorkIqHostPolicy.OptInKey, "true"),
                ("HumanWorkIQ:Enabled", "true"), ("PrivateAnalysis:Enabled", "true"),
                ("NaturalLanguage:DirectMcp:Principal", "SignedInHuman"));
            IConfiguration config = new ConfigurationBuilder().AddConfiguration(lower).AddInMemoryCollection(
                new Dictionary<string, string?> { [WorkIqHostPolicy.OptInKey] = "false",
                    ["NaturalLanguage:DirectMcp:Principal"] = "AgentUser",
                    ["NaturalLanguage:Enabled"] = "false" }).Build();
            var host = StartupPolicy(config);
            Must(host.PrincipalConfigured && host.Principal == "AgentUser" && !host.AllowHumanIdentity &&
                !host.HumanOAuthEnabled && !host.PrivateJobsEnabled &&
                !config.GetSection("NaturalLanguage").Get<NaturalLanguageOptions>()!.Enabled);
            return Task.CompletedTask;
        });
        await check("SignedInHuman opt-in still requires explicit human OAuth activation", () =>
        {
            try { _ = StartupPolicy(StartupConfig((WorkIqHostPolicy.OptInKey, "true"),
                ("HumanWorkIQ:Enabled", "false"), ("NaturalLanguage:DirectMcp:Principal", "SignedInHuman"))); }
            catch (InvalidOperationException error)
            {
                Must(error.Message.Contains("HumanWorkIQ:Enabled=true") && error.Message.Contains("No identity fallback"));
                return Task.CompletedTask;
            }
            throw new Exception("Human OAuth false was overridden.");
        });
        foreach (bool opted in new[] { false, true })
            foreach (bool oauth in new[] { false, true })
                foreach (bool jobs in new[] { false, true })
                    await check($"WorkIQ registration matrix opt-in={opted} OAuth={oauth} private={jobs}", async () =>
                    {
                        await using Harness h = new();
                        var config = StartupConfig((WorkIqHostPolicy.OptInKey, opted.ToString()),
                            ("HumanWorkIQ:Enabled", oauth.ToString()), ("PrivateAnalysis:Enabled", jobs.ToString()));
                        var host = StartupPolicy(config);
                        ServiceCollection services = new();
                        host.RegisterHuman(services, config, h.HumanSettings);
                        var privateOptions = new PrivateAnalysisOptions { Enabled = jobs, StorageDirectory = "NEVER-OPEN-SECRET" };
                        host.RegisterPrivate(services, privateOptions, h.Settings);
                        Must(services.Any(s => s.ServiceType == typeof(HumanSettings)) == (opted && oauth));
                        Must(services.Any(s => s.ServiceType == typeof(PrivateAnalysisCoordinator)) == (opted && oauth && jobs));
                        Must(services.Any(s => s.ServiceType == typeof(IHostedService) &&
                            s.ImplementationType == typeof(PrivateAnalysisWorker)) == (opted && oauth && jobs));
                        Must(privateOptions.Enabled == (opted && oauth && jobs));
                        if (!opted || !oauth) Must(services.Count == 0);
                        services.AddRouting();
                        services.AddLogging();
                        using ServiceProvider provider = services.BuildServiceProvider();
                        StartupEndpoints endpoints = new(provider);
                        host.MapHuman(endpoints);
                        Must((endpoints.DataSources.Count != 0) == (opted && oauth));
                        Must(!host.Status.Contains("SECRET") && !host.Status.Contains(Harness.HumanClient) &&
                            !host.Status.Contains("synthetic-human-secret"));
                    });
        await check("WorkIQ retired flags across all contexts clear only recipe activations and emit bounded names", async () =>
        {
            await using Harness h = new();
            string[] flags = ["AutoExecuteFolderCreateTestMode", "AutoExecuteRenameTestMode", "AutoExecuteMoveTestMode",
                "AutoExecuteGroupShareTestMode", "SharingRosterDiscoveryOnly", "UploadMappingDiagnosticEnabled",
                "SectionTwoEnabled", "GroupShareEnabled", "AutoChannelFolderCreateEnabled", "AutoChannelRenameEnabled",
                "AutoChannelMoveEnabled", "AutoChannelArtifactCreateEnabled", "AutoChannelContentEditEnabled"];
            string Acl() => JsonSerializer.Serialize(h.Policy.Contexts.Select(c =>
                new { c.ConversationId, c.Type, c.TeamId, c.ChannelId, c.RequesterIds, c.Roots, c.EnableMutations }));
            h.Policy.EnableMutations = false;
            foreach (var context in h.Policy.Contexts) context.EnableMutations = false;
            string acl = Acl();
            foreach (var context in h.Policy.Contexts)
                foreach (string flag in flags) typeof(ContextPolicy).GetProperty(flag)!.SetValue(context, true);
            var config = StartupConfig(("SetupMode:Enabled", "true"), ("ChannelSetup:Enabled", "true"),
                ("SetupMode:PrivateSecret", "DO-NOT-LOG"), ("FilePolicy:Contexts:private-secret-key:Other", "DO-NOT-LOG"));
            string warning = RetiredWorkIqConfiguration.Apply(config, h.Policy);
            h.Policy.Validate(allowInactiveGroupContexts: true);
            Must(Acl() == acl && !h.Policy.EnableMutations && warning.Length < 1600 &&
                !warning.Contains("DO-NOT-LOG") && !warning.Contains("private-secret-key") &&
                !warning.Contains(Harness.Human) && config.GetValue<bool>("SetupMode:Enabled"));
            foreach (var context in h.Policy.Contexts)
                foreach (string flag in flags) Must((bool)typeof(ContextPolicy).GetProperty(flag)!.GetValue(context)! == false);
            Must(warning.Contains("AutoExecuteGroupShareTestMode") && warning.Split("AutoExecuteGroupShareTestMode").Length == 2);
            await Denied(() => Task.FromResult(h.Policy.Authorize(Harness.Activity("read", user: Harness.Au), h.Settings)));
            await Denied(() => Task.FromResult(h.Router.NaturalLanguageAu(
                h.Policy.Authorize(Harness.Activity("read", "groupChat"), h.Settings))));
            Must(h.Handler.CatalogCalls == 0 && h.Handler.ToolCalls == 0);
        });
        await check("Retired rootless roster group remains configured but cannot gain native data access", async () =>
        {
            await using Harness h = new();
            ContextPolicy group = h.Policy.Contexts.Single(c => c.Type == "groupChat");
            group.SharingRosterDiscoveryOnly = true;
            group.Roots = [];
            _ = RetiredWorkIqConfiguration.Apply(StartupConfig(), h.Policy);
            h.Policy.Validate(allowInactiveGroupContexts: true);
            await Denied(() => Task.FromResult(h.Router.NaturalLanguageAu(
                h.Policy.Authorize(Harness.Activity("read", "groupChat"), h.Settings))));
            try { h.Policy.Validate(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Other providers lost their existing root requirement.");
        });
        await check("Dormant group rejects personal-context reuse and opted-in human data or handoff", async () =>
        {
            await using PrivateHarness p = new();
            ContextPolicy group = p.H.Policy.Contexts.Single(c => c.Type == "groupChat");
            group.SharingRosterDiscoveryOnly = true;
            group.Roots = [];
            _ = RetiredWorkIqConfiguration.Apply(StartupConfig(), p.H.Policy);
            p.H.Policy.Validate(allowInactiveGroupContexts: true);
            var host = StartupPolicy(StartupConfig((WorkIqHostPolicy.OptInKey, "true"),
                ("HumanWorkIQ:Enabled", "true"), ("PrivateAnalysis:Enabled", "true")));
            WorkIqRouter router = new(p.H.AuSession, p.Humans, p.H.HumanSettings, p.H.Settings, hostPolicy: host);
            var activity = Harness.Activity("Read my human files", "groupChat");
            Invocation invocation = p.H.Policy.Authorize(activity, p.H.Settings);
            foreach (string principal in new[] { "AgentUser", "SignedInHuman" })
                await Denied(async () => { await using var lease = await router.AcquireDirect(invocation, principal, default); });
            var spoof = JsonSerializer.SerializeToNode(activity, JsonSerializerOptions.Web)!;
            spoof["conversation"]!["conversationType"] = "personal";
            await Denied(() => Task.FromResult(p.H.Policy.Authorize(
                spoof.Deserialize<Microsoft.Teams.Apps.Schema.MessageActivity>(JsonSerializerOptions.Web)!, p.H.Settings)));
            var wrongTenant = JsonSerializer.SerializeToNode(activity, JsonSerializerOptions.Web)!;
            wrongTenant["channelData"]!["tenant"]!["id"] = Harness.Other;
            await Denied(() => Task.FromResult(p.H.Policy.Authorize(
                wrongTenant.Deserialize<Microsoft.Teams.Apps.Schema.MessageActivity>(JsonSerializerOptions.Web)!, p.H.Settings)));
            Must(!(await p.Coordinator.Handle(activity, default)).Handled);
            Must(p.Store.List().Length == 0 && p.H.Cache.Acquired.Count == 0 && p.Routing.Requests.Count == 0 &&
                p.H.Handler.CatalogCalls == 0 && p.H.Handler.ToolCalls == 0 && p.Ui.Wire.Requests.Count == 0);
            group.RequesterIds = ["not-a-guid"];
            try { p.H.Policy.Validate(allowInactiveGroupContexts: true); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Dormant group bypassed malformed requester validation.");
        });
        foreach (string command in new[] { "/signin", "/disconnect", "/private-jobs", "/help", "/status" })
            await check("WorkIQ AU-default controls cannot access stale human sessions: " + command, async () =>
            {
                await using Harness h = new(); await h.Connect(Harness.Human);
                int acquired = h.Cache.Acquired.Count;
                var host = StartupPolicy(StartupConfig(("HumanWorkIQ:Enabled", "true"), ("PrivateAnalysis:Enabled", "true")));
                WorkIqRouter router = new(h.AuSession, h.Connections, h.HumanSettings, h.Settings,
                    WorkIqHostPolicy.HumanUnavailable, host);
                ModelHandler model = new();
                NaturalLanguageAgent native = new(DirectOptions(), router, h.Policy, h.Settings, new(), new(),
                    o => new(o, model));
                WorkIqIngress ingress = new(router, h.Settings, h.Policy, native);
                string result = (await ingress.Handle(Harness.Activity(command, "personal"), default))!;
                Must(!result.Contains("https://") && !result.Contains("synthetic-human-secret") &&
                    (result.Contains("inactive") || result.Contains("opt-in=False") || result.Contains("unavailable")));
                Must(h.Cache.Acquired.Count == acquired && h.Cache.Removed.Count == 0 &&
                    h.Handler.CatalogCalls == 0 && h.Handler.ToolCalls == 0 && model.Requests.Count == 0);
                await Denied(async () => { await using var lease = await router.AcquireDirect(
                    h.Policy.Authorize(Harness.Activity("read", "personal"), h.Settings), "SignedInHuman", default); });
            });
        foreach (bool humanRequest in new[] { false, true })
            await check("AU-default main model keeps original call structure and capability context human-request=" + humanRequest, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                var host = StartupPolicy(StartupConfig(("HumanWorkIQ:Enabled", "true"), ("PrivateAnalysis:Enabled", "true")));
                WorkIqRouter router = new(h.AuSession, h.Connections, h.HumanSettings, h.Settings,
                    WorkIqHostPolicy.HumanUnavailable, host);
                string text = humanRequest ? "Authenticate as my human identity and inspect my OneDrive." : "Use the AU to read this file.";
                ModelHandler model = humanRequest ? new(Completion("Human identity is unavailable; no human sign-in or data action was started.")) :
                    new(Completion(tool: "fetch", args: Fetch("/drives/drive/items/file")), Completion("AU metadata observed."));
                NaturalLanguageAgent native = new(DirectOptions(), router, h.Policy, h.Settings, new(), new(), o => new(o, model));
                string result = (await native.Handle(Harness.Activity(text, "personal"), default))!;
                Must(model.Requests.Count == (humanRequest ? 1 : 2) && h.Handler.CatalogCalls == 1 &&
                    h.Handler.ToolCalls == (humanRequest ? 0 : 1) && h.Cache.Acquired.Count == 0);
                var messages = model.Requests[0].GetProperty("messages").EnumerateArray().ToArray();
                Must(messages[0].GetProperty("content").GetString()!.Contains("Human identity is not enabled") &&
                    messages.Any(m => m.GetProperty("role").GetString() == "user" && m.GetProperty("content").GetString() == text));
                if (humanRequest) Must(result.Contains("Human identity is unavailable"));
                Must(!model.Requests[0].GetProperty("tools").GetRawText().Contains("request_human_analysis"));
            });
        foreach (string principal in new[] { "AgentUser", "SignedInHuman" })
            await check("Explicit human opt-in retains fixed native principal " + principal, async () =>
            {
                await using Harness h = new(); DirectEnable(h); await h.Connect(Harness.Human);
                int before = h.Cache.Acquired.Count;
                var host = StartupPolicy(StartupConfig((WorkIqHostPolicy.OptInKey, "true"),
                    ("HumanWorkIQ:Enabled", "true"), ("NaturalLanguage:DirectMcp:Principal", principal)));
                WorkIqRouter router = new(h.AuSession, h.Connections, h.HumanSettings, h.Settings, hostPolicy: host);
                await using var lease = await router.AcquireDirect(
                    h.Policy.Authorize(Harness.Activity("read", "personal"), h.Settings), principal, default);
                _ = await lease.Backend.ListAsync(default);
                Must(lease.Route.Human == (principal == "SignedInHuman") && h.Handler.CatalogCalls == 1 &&
                    (principal == "SignedInHuman" ? h.Cache.Acquired.Count > before : h.Cache.Acquired.Count == before));
                foreach (string token in h.Handler.Bearers)
                    if (principal == "SignedInHuman")
                        h.HumanSettings.ValidateAccessToken(token, h.HumanSettings.Key(
                            h.Policy.Authorize(Harness.Activity("read", "personal"), h.Settings)), Harness.Principal(Harness.Human));
                    else AgentUserTokenProvider.ValidateTokenShape(token, h.Settings);
            });
        await check("Opt-in off leaves ready persisted private job and credentials untouched without registration or resumption", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate();
            await p.Authenticate(await p.Approve(job));
            Must(p.Store.Get(job.Id).State == PrivateJobState.Ready);
            byte[] bytes = p.Storage.Bytes!.ToArray();
            int cache = p.H.Cache.Acquired.Count, ui = p.Ui.Wire.Requests.Count, routing = p.Routing.Requests.Count;
            var host = StartupPolicy(StartupConfig(("HumanWorkIQ:Enabled", "true"), ("PrivateAnalysis:Enabled", "true")));
            WorkIqRouter router = new(p.H.AuSession, p.Humans, p.H.HumanSettings, p.H.Settings, hostPolicy: host);
            using PrivateAnalysisWorker worker = new(p.Store, p.Authorization, p.Humans, router, p.Teams,
                p.ModelOptions, p.H.Policy, p.H.Settings, new(), new(), p.H.Clock,
                NullLogger<PrivateAnalysisWorker>.Instance, o => new(o, p.Model));
            PrivateAnalysisCoordinator coordinator = new(p.Options, p.Store, p.Authorization, p.Humans,
                p.H.HumanSettings, p.Teams, new(p.ModelOptions, o => new(o, p.Routing)), p.H.Policy, p.H.Settings,
                p.H.Clock, p.Log, p.ModelOptions, host);
            Must(!await worker.RunOne(default));
            Must((await coordinator.Handle(p.Personal, default)).Reply!.Contains("inactive"));
            Must((await coordinator.Handle(p.Origin, default)).Reply!.Contains("inactive"));
            await Denied(() => coordinator.Action(p.Personal, "lab13.private.approve", job.Id, job.Revision, default));
            Must(bytes.SequenceEqual(p.Storage.Bytes!) && p.Store.Get(job.Id).State == PrivateJobState.Ready &&
                p.H.Cache.Acquired.Count == cache && p.Ui.Wire.Requests.Count == ui &&
                p.Routing.Requests.Count == routing && p.Model.Requests.Count == 0 && p.H.Handler.ToolCalls == 0);
        });
        await check("Explicit human opt-in preserves private per-job approval and existing consent scope", async () =>
        {
            await using PrivateHarness p = new();
            var host = StartupPolicy(StartupConfig((WorkIqHostPolicy.OptInKey, "true"),
                ("HumanWorkIQ:Enabled", "true"), ("PrivateAnalysis:Enabled", "true")));
            PrivateAnalysisCoordinator coordinator = new(p.Options, p.Store, p.Authorization, p.Humans,
                p.H.HumanSettings, p.Teams, new(p.ModelOptions, o => new(o, p.Routing)), p.H.Policy, p.H.Settings,
                p.H.Clock, p.Log, p.ModelOptions, host);
            _ = await coordinator.Handle(p.Origin, default);
            PrivateJob job = p.Store.List().Single();
            Must(job.State == PrivateJobState.AwaitingApproval && job.ConsentUntil is null && !job.HasRun);
            WorkIqRouter router = new(p.H.AuSession, p.Humans, p.H.HumanSettings, p.H.Settings, hostPolicy: host);
            using PrivateAnalysisWorker worker = new(p.Store, p.Authorization, p.Humans, router, p.Teams,
                p.ModelOptions, p.H.Policy, p.H.Settings, new(), new(), p.H.Clock,
                NullLogger<PrivateAnalysisWorker>.Instance, o => new(o, p.Model));
            Must(!await worker.RunOne(default));
            await p.Authenticate(await p.Approve(job));
            Must(await worker.RunOne(default) && p.Store.Get(job.Id).State == PrivateJobState.Completed &&
                p.Store.Get(job.Id).ConsentScope == PrivateConsentScope.NativeReadWriteV1);
        });
    }
}
