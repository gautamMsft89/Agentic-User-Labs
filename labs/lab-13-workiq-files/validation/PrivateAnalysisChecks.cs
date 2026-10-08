using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Apps.Handlers;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private sealed class MemoryPrivateStorage : IPrivateStateStorage
    {
        internal byte[]? Bytes;
        internal bool Fail;
        public byte[]? Load() => Bytes?.ToArray();
        public void Save(byte[] bytes) { if (Fail) throw new IOException("PRIVATE"); Bytes = bytes.ToArray(); }
        public void Dispose() { }
    }
    private sealed class PrivateHarness : IAsyncDisposable
    {
        internal readonly Harness H = new();
        internal readonly MemoryPrivateStorage Storage = new();
        internal readonly PrivateAnalysisOptions Options = new() { Enabled = true };
        internal readonly NaturalLanguageOptions ModelOptions = DirectOptions();
        internal readonly PrivateJobStore Store;
        internal readonly PrivateAuthorizations Authorization;
        internal readonly HumanConnections Humans;
        internal readonly UiFixture Ui;
        internal readonly PrivateTeams Teams;
        internal readonly PrivateAnalysisCoordinator Coordinator;
        internal readonly PrivateAnalysisWorker Worker;
        internal readonly ModelHandler Routing;
        internal readonly ModelHandler Model;
        internal readonly PrivateLog<PrivateAnalysisCoordinator> Log = new();
        internal readonly MessageActivity Origin = ProgressActivity("Analyze my private OneDrive personal.txt");
        internal readonly MessageActivity Personal = ProgressActivity("/private-jobs", "personal");
        internal PrivateHarness(ModelHandler? model = null, ModelHandler? routing = null)
        {
            DirectEnable(H);
            Store = new(Storage, H.Clock);
            Authorization = new(Store, Options, H.Policy, H.Settings, H.HumanSettings, ModelOptions, H.Clock);
            Humans = new(H.HumanSettings, H.Cache, new(tokens => new WorkIqSession(tokens, H.Handler)),
                H.Clock, Authorization);
            Ui = new(H, Origin);
            Ui.Wire.Respond = (request, _) => Task.FromResult(UiTransport.Ok(
                request.RequestUri!.AbsolutePath == "/v3/conversations" ? "19:personal" :
                    request.Method == HttpMethod.Post ? "private-own-" + Ui.Wire.Requests.Count : "updated"));
            Teams = new(Ui.Core, H.Policy, H.Settings);
            Routing = routing ?? new(Completion(tool: "request_human_analysis", args: new { }));
            Model = model ?? new(Completion(tool: "fetch", args: Fetch("/drives/human-drive/items/human-file")),
                Completion("Observed private file metadata."));
            Coordinator = new(Options, Store, Authorization, Humans, H.HumanSettings, Teams,
                new(ModelOptions, o => new NaturalLanguageModel(o, Routing)), H.Policy, H.Settings, H.Clock,
                Log, ModelOptions);
            WorkIqRouter router = new(H.AuSession, Humans, H.HumanSettings, H.Settings);
            Worker = new(Store, Authorization, Humans, router, Teams, ModelOptions, H.Policy, H.Settings, new(), new(),
                H.Clock, NullLogger<PrivateAnalysisWorker>.Instance, o => new NaturalLanguageModel(o, Model));
        }
        internal async Task<PrivateJob> Initiate()
        {
            PrivateIngress outcome = await Coordinator.Handle(Origin, default);
            Must(outcome.Handled && outcome.Reply is PrivateAnalysisCoordinator.Neutral or PrivateAnalysisCoordinator.CardSent);
            return Store.List().Single();
        }
        internal async Task<string> Approve(PrivateJob job)
        {
            PrivateActionResult result = await Coordinator.Action(Personal, "lab13.private.approve", job.Id, job.Revision, default);
            JsonElement card = result.Card ?? throw new Exception("Expected private sign-in invoke card.");
            return card.GetProperty("actions")[0].GetProperty("url").GetString()!;
        }
        internal PrivateJob Legacy(PrivateJob job)
        {
            Authorization.Cancel(job.Id);
            return Store.Add(job with { Id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)),
                ConsentScope = PrivateConsentScope.LegacyReadOnly });
        }
        internal async Task Authenticate(string url, string owner = Harness.Human)
        {
            string ticket = new Uri(url).Query["?ticket=".Length..];
            string flow = Humans.Begin(ticket);
            await Humans.Complete(flow, Harness.Principal(owner), default);
        }
        public async ValueTask DisposeAsync()
        {
            Worker.Dispose(); await Humans.DisposeAsync(); Store.Dispose(); Ui.Dispose(); await H.DisposeAsync();
        }
    }
    private static async Task PrivateAnalysisChecks(Func<string, Func<Task>, Task> check)
    {
        await PrivateNativeScopeChecks(check);
        await PrivateStateBoundaryChecks(check);
        await PrivateInvokeWireChecks(check);
        await PrivateIngressDiagnosticChecks(check);
        await check("Private channel handoff private approval OAuth resume and final use native human reads only", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate();
            Must(job.State == PrivateJobState.AwaitingApproval && p.H.Handler.ToolCalls == 0 && p.Model.Requests.Count == 0);
            Must(p.Ui.Wire.Requests.Count == 2 && p.Ui.Wire.Requests[0].Url.AbsolutePath == "/v3/conversations");
            Must(p.Ui.Wire.Requests[1].Body.GetProperty("conversation").GetProperty("conversationType").GetString() == "personal");
            string url = await p.Approve(job);
            Must(p.Store.Get(job.Id).State == PrivateJobState.AwaitingSignIn && !await p.Worker.RunOne(default));
            await p.Authenticate(url);
            Must(p.Store.Get(job.Id).State == PrivateJobState.Ready);
            Must(await p.Worker.RunOne(default));
            PrivateJob done = p.Store.Get(job.Id);
            Must(done.State == PrivateJobState.Completed && done.HasRun && done.Result!.Contains("Observed private file metadata."));
            Must(p.Model.Requests.Count == 2 && p.H.Handler.ToolCalls == 1 && p.H.Handler.Mutations == 0);
            foreach (string token in p.H.Handler.Bearers)
                p.H.HumanSettings.ValidateAccessToken(token, p.H.HumanSettings.Key(p.H.Policy.Authorize(p.Personal, p.H.Settings)), Harness.Principal(Harness.Human));
            foreach (UiRequest request in p.Ui.Wire.Requests)
            {
                Must(request.Identity?.AgenticUserId == Harness.Au && request.Identity.AgenticAppId == Harness.Agent);
                if (request.Url.AbsolutePath == "/v3/conversations") continue;
                Must(request.Body.GetProperty("conversation").GetProperty("id").GetString() == "19:personal" &&
                    !Uri.UnescapeDataString(request.Url.AbsolutePath).Contains(Harness.Channel));
            }
            Must(!await p.Worker.RunOne(default) && p.H.Handler.ToolCalls == 1);
            JsonElement model = p.Model.Requests[0];
            Must(model.GetProperty("tools").EnumerateArray().Any(t =>
                t.GetProperty("function").GetProperty("name").GetString() == "create_entity"));
            Must(model.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("NativeReadWriteV1"));
        });
        await check("Private input router exposes three app choices with full guide but no live catalog metadata or transcript", async () =>
        {
            await using PrivateHarness p = new();
            await p.Initiate();
            JsonElement request = p.Routing.Requests.Single();
            Must(request.GetProperty("tools").GetArrayLength() == 3 && request.GetProperty("messages").GetArrayLength() == 2 &&
                request.GetProperty("messages")[1].GetProperty("content").GetString() == p.Origin.TextWithoutMentions &&
                p.H.Handler.Initializes == 0 && p.H.Handler.CatalogCalls == 0);
            Must(!request.GetRawText().Contains(Harness.Channel) &&
                request.GetProperty("tools").EnumerateArray().All(t =>
                    t.GetProperty("function").GetProperty("name").GetString() is
                        "request_human_analysis" or "continue_current_profile" or "clarify_workiq"));
        });
        await check("Private continue choice retains normal Direct path without private state or Teams calls", async () =>
        {
            await using PrivateHarness p = new(routing: new(Completion(tool: "continue_current_profile", args: new { })));
            PrivateIngress result = await p.Coordinator.Handle(p.Origin, default);
            Must(!result.Handled && p.Store.List().Length == 0 && p.Ui.Wire.Requests.Count == 0 && p.H.Handler.ToolCalls == 0);
        });
        foreach (string choice in new[] { "recipient", "rewritten-request", "unknown-tool", "plain-text" })
            await check("Private router cannot choose task identity recipient or unsupported result " + choice, async () =>
            {
                JsonObject completion = choice switch
                {
                    "plain-text" => Completion("Private payroll result"),
                    "unknown-tool" => Completion(tool: "fetch", args: Fetch("/me/drive")),
                    _ => Completion(tool: "request_human_analysis", args: new Dictionary<string, string>
                        { [choice] = "untrusted replacement" })
                };
                await using PrivateHarness p = new(routing: new(completion));
                await Denied(async () => await p.Coordinator.Handle(p.Origin, default));
                Must(p.Store.List().Length == 0 && p.Ui.Wire.Requests.Count == 0 && p.H.Handler.ToolCalls == 0);
            });
        foreach (string failure in new[] { "403", "unapproved-chat", "missing-id", "changed-service", "create-io" })
            await check("Private unavailable proactive route preserves neutral personal-chat fallback " + failure, async () =>
            {
                await using PrivateHarness p = new();
                p.Ui.Wire.Respond = (_, _) => failure switch
                {
                    "403" => Task.FromResult(UiTransport.Fail(HttpStatusCode.Forbidden)),
                    "unapproved-chat" => Task.FromResult(UiTransport.Ok("19:unapproved")),
                    "missing-id" => Task.FromResult(UiTransport.Ok("")),
                    "create-io" => throw new IOException("PRIVATE"),
                    _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"id":"19:personal","serviceUrl":"https://evil.invalid/"}""") })
                };
                PrivateJob job = await p.Initiate();
                Must(job.State == PrivateJobState.AwaitingRoute && job.Personal is null && p.Ui.Wire.Requests.Count == 1 &&
                    p.H.Handler.ToolCalls == 0 && p.Model.Requests.Count == 0);
                p.Ui.Wire.Respond = (_, _) => Task.FromResult(UiTransport.Ok("recovered-own"));
                await p.Coordinator.Handle(p.Personal, default);
                Must(p.Store.Get(job.Id).State == PrivateJobState.AwaitingApproval &&
                    p.Ui.Wire.Requests.Last().Body.GetProperty("conversation").GetProperty("id").GetString() == "19:personal");
            });
        await check("Private original channel replay never creates or routes twice", async () =>
        {
            await using PrivateHarness p = new();
            await p.Initiate(); await p.Coordinator.Handle(p.Origin, default);
            Must(p.Routing.Requests.Count == 1 && p.Ui.Wire.Requests.Count == 2 && p.Store.List().Length == 1);
        });
        await check("Private ordinary signin alone does not authorize pending analysis", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate();
            var invocation = p.H.Policy.Authorize(p.Personal, p.H.Settings);
            string link = await p.Humans.CreateLink(invocation, default);
            await p.Authenticate(BrowserSignIn.PromptUrl(link, p.H.HumanSettings)!);
            Must(p.Store.Get(job.Id).State == PrivateJobState.AwaitingApproval && !await p.Worker.RunOne(default));
            await p.Coordinator.Action(p.Personal, "lab13.private.approve", job.Id, job.Revision, default);
            Must(p.Store.Get(job.Id).State == PrivateJobState.Ready);
        });
        foreach (string mismatch in new[] { "human", "tenant", "channel", "revision", "recipient" })
            await check("Private approval denies mismatched authenticated context " + mismatch, async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob job = await p.Initiate();
                MessageActivity action = PrivateContext.Activity(PrivateContext.Capture(p.Personal));
                if (mismatch == "human") action.From!.AadObjectId = Harness.Other;
                if (mismatch == "channel") action = p.Origin;
                if (mismatch == "recipient") action.Recipient!.AgenticUserId = Harness.Other;
                if (mismatch == "tenant")
                {
                    JsonObject node = JsonSerializer.SerializeToNode(action, JsonSerializerOptions.Web)!.AsObject();
                    node["channelData"]!["tenant"]!["id"] = Harness.Other;
                    action = node.Deserialize<MessageActivity>(JsonSerializerOptions.Web)!;
                }
                await Denied(async () => await p.Coordinator.Action(action, "lab13.private.approve", job.Id,
                    job.Revision + (mismatch == "revision" ? 1 : 0), default));
                Must(p.Store.Get(job.Id).State == PrivateJobState.AwaitingApproval && p.Model.Requests.Count == 0);
            });
        foreach (string failure in new[] { "wrong-human", "wrong-tenant", "expired", "cancelled", "policy-change", "disconnect" })
            await check("Private purpose-bound OAuth cannot resume invalidated grant " + failure, async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob job = await p.Initiate(); string url = await p.Approve(job);
                string ticket = new Uri(url).Query["?ticket=".Length..];
                string flow = p.Humans.Begin(ticket);
                if (failure == "expired") p.H.Clock.Now += TimeSpan.FromMinutes(6);
                if (failure == "cancelled") p.Authorization.Cancel(job.Id);
                if (failure == "policy-change") p.H.Policy.MaxItems++;
                if (failure == "disconnect") await p.Humans.Disconnect(p.H.Policy.Authorize(p.Personal, p.H.Settings));
                await Denied(() => p.Humans.Complete(flow, Harness.Principal(
                    failure == "wrong-human" ? Harness.Other : Harness.Human,
                    failure == "wrong-tenant" ? Harness.Other : Harness.Tenant), default));
                Must(p.Store.Get(job.Id).State != PrivateJobState.Ready && p.Model.Requests.Count == 0);
            });
        await check("Private callback and approval consume one execution claim", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate(); string url = await p.Approve(job);
            string ticket = new Uri(url).Query["?ticket=".Length..], flow = p.Humans.Begin(ticket);
            await Denied(async () => { _ = p.Humans.Begin(ticket); await Task.CompletedTask; });
            await p.Humans.Complete(flow, Harness.Principal(Harness.Human), default);
            await Denied(() => p.Humans.Complete(flow, Harness.Principal(Harness.Human), default));
            PrivateActionResult duplicate = await p.Coordinator.Action(p.Personal, "lab13.private.approve", job.Id, job.Revision, default);
            Must(duplicate.Card is null && duplicate.Message.Contains("already accepted"));
            await p.Worker.RunOne(default);
            Must(!await p.Worker.RunOne(default) && p.Model.Requests.Count == 2 && p.H.Handler.ToolCalls == 1);
        });
        foreach (string forbidden in new[] { "create_entity", "do_action", "ask", "request_human_analysis", "mail", "other-user", "share", "conversion" })
            await check("Private analysis rejects model writes identity handoff and nonfile families " + forbidden, async () =>
            {
                string tool = forbidden switch
                { "mail" or "other-user" or "share" => "fetch", "conversion" => "fetch_blob", _ => forbidden };
                object args = forbidden switch
                {
                    "mail" => Fetch("/me/messages"), "other-user" => Fetch("/users/" + Harness.Other + "/drive"),
                    "share" => Fetch("/shares/u!fake/driveItem"),
                    "conversion" => new { path = "/drives/human-drive/items/human-file/content", format = "pdf" },
                    _ => CreateArgs()
                };
                await using PrivateHarness p = new(new(Completion(tool: tool, args: args)));
                PrivateJob job = p.Legacy(await p.Initiate()); await p.Authenticate(await p.Approve(job));
                await p.Worker.RunOne(default);
                Must(p.H.Handler.ToolCalls == 0 && p.H.Handler.Mutations == 0 && p.Model.Requests.Count == 1);
                Must(p.Store.Get(job.Id).Result is not null && !p.Store.Get(job.Id).Result!.Contains("AI answer"));
            });
        await check("Private delivery failure recovers persisted output without analysis replay", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
            p.Ui.Wire.Respond = (_, _) => Task.FromResult(p.Ui.Wire.Requests.Last().Body.TryGetProperty("text", out _) ?
                UiTransport.Fail(HttpStatusCode.BadRequest) : UiTransport.Ok());
            await p.Worker.RunOne(default);
            Must(p.Store.Get(job.Id).State == PrivateJobState.DeliveryPending && p.Store.Get(job.Id).Result is not null);
            int before = p.Model.Requests.Count, calls = p.H.Handler.ToolCalls;
            p.Ui.Wire.Respond = (_, _) => Task.FromResult(UiTransport.Ok());
            await p.Coordinator.Handle(p.Personal, default);
            PrivateJob retry = p.Store.Get(job.Id);
            await p.Coordinator.Action(p.Personal, "lab13.private.approve", retry.Id, retry.Revision, default);
            await p.Worker.RunOne(default);
            Must(p.Store.Get(job.Id).State == PrivateJobState.Completed && p.Model.Requests.Count == before && p.H.Handler.ToolCalls == calls);
        });
        await check("Private state restart requires reauthorization and never replays a running analysis", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
            MemoryPrivateStorage readySnapshot = new() { Bytes = p.Storage.Bytes!.ToArray() };
            using PrivateJobStore restarted = new(readySnapshot, p.H.Clock);
            Must(restarted.Get(job.Id).State == PrivateJobState.AwaitingApproval &&
                restarted.Get(job.Id).HumanGeneration is null && restarted.Get(job.Id).Revision > p.Store.Get(job.Id).Revision);
            p.Store.Change(job.Id, j => j with { State = PrivateJobState.Running, HasRun = true });
            using PrivateJobStore interrupted = new(new MemoryPrivateStorage { Bytes = p.Storage.Bytes!.ToArray() }, p.H.Clock);
            Must(interrupted.Get(job.Id).State == PrivateJobState.Interrupted && interrupted.Get(job.Id).HasRun);
        });
        await check("Private store malformed data and failed writes fail closed", async () =>
        {
            bool malformed = false;
            try { using PrivateJobStore bad = new(new MemoryPrivateStorage { Bytes = Encoding.UTF8.GetBytes("not-json") }, TimeProvider.System); }
            catch (LabException) { malformed = true; }
            Must(malformed);
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate();
            p.Storage.Fail = true;
            await Denied(async () => { p.Store.Change(job.Id, j => j with { State = PrivateJobState.Cancelled }); await Task.CompletedTask; });
            await Denied(async () => { _ = p.Store.Get(job.Id); await Task.CompletedTask; });
            Must(p.Model.Requests.Count == 0);
        });
        await check("Private resumed native blob text enters actual human-only model request", async () =>
        {
            await using PrivateHarness p = new(new(
                Completion(tool: "fetch_blob", args: new { path = "/drives/human-drive/items/human-file/content" }),
                Completion("Private text described.")));
            PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
            await p.Worker.RunOne(default);
            Must(p.Store.Get(job.Id).State == PrivateJobState.Completed && p.H.Handler.Calls.Single().Tool == "fetch_blob" &&
                p.Model.Requests[1].GetRawText().Contains("WorkIQ Lab 13 synthetic file.") && p.ModelOptions.DirectMcp.Principal == "AgentUser",
                p.Store.Get(job.Id).Result ?? p.Store.Get(job.Id).State.ToString());
        });
        foreach (string mode in new[] { "manual", "automatic", "wrong-type", "override-data", "wrong-context",
            "missing-tenant", "missing-au", "unsafe-service", "wrong-invoke" })
            await check("Private real SDK adaptive invoke accepts only explicit matching personal approval " + mode, async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob job = await p.Initiate();
                JsonObject activity = JsonSerializer.SerializeToNode(mode == "wrong-context" ? p.Origin : p.Personal, JsonSerializerOptions.Web)!.AsObject();
                activity["type"] = "invoke"; activity["name"] = "adaptiveCard/action";
                if (mode == "missing-tenant") activity["channelData"] = null;
                if (mode == "missing-au") activity["recipient"]!.AsObject()["agenticAppId"] = null;
                if (mode == "unsafe-service") activity["serviceUrl"] = "https://teams.example.invalid/?secret=synthetic";
                if (mode == "wrong-invoke") activity["name"] = "task/fetch";
                JsonObject data = new() { ["job"] = job.Id, ["revision"] = job.Revision };
                if (mode == "override-data") data["recipient"] = Harness.Other;
                activity["value"] = new JsonObject
                {
                    ["trigger"] = mode == "automatic" ? "automatic" : "manual",
                    ["action"] = new JsonObject { ["type"] = mode == "wrong-type" ? "Action.OpenUrl" : "Action.Execute",
                        ["verb"] = "lab13.private.approve", ["data"] = data }
                };
                InvokeActivity invoke = InvokeActivity.FromActivity(
                    activity.Deserialize<Microsoft.Teams.Core.Schema.CoreActivity>(JsonSerializerOptions.Web)!);
                if (mode == "manual")
                {
                    Must(invoke.From?.AadObjectId == p.Personal.From!.AadObjectId && invoke.ChannelData?.Tenant?.Id == Harness.Tenant,
                        JsonSerializer.Serialize(invoke, JsonSerializerOptions.Web));
                    PrivateActionResult result = await p.Coordinator.Action(invoke, default);
                    Must(result.Card is not null && result.Message.Contains("private sign-in card") && p.Store.Get(job.Id).State == PrivateJobState.AwaitingSignIn);
                }
                else
                {
                    await Denied(async () => await p.Coordinator.Action(invoke, default));
                    Must(p.Store.Get(job.Id).State == PrivateJobState.AwaitingApproval);
                }
                Must(p.Model.Requests.Count == 0 && p.H.Handler.ToolCalls == 0);
            });
        await check("Private attachment snapshot retains provenance but no raw URL card bytes or credentials", async () =>
        {
            await using PrivateHarness p = new();
            p.Origin.Attachments = AttachedActivity("unused", new JsonArray(FileAttachment("personal.txt"))).Attachments;
            PrivateJob job = await p.Initiate();
            string persisted = Encoding.UTF8.GetString(p.Storage.Bytes!);
            Must(job.Metadata.Contains("personal.txt") && !persisted.Contains("PRIVATE") &&
                !persisted.Contains("downloadUrl") && !job.Origin.GetRawText().Contains("attachments") &&
                !p.Routing.Requests[0].GetRawText().Contains("personal.txt?"));
            string url = await p.Approve(job);
            string ticket = new Uri(url).Query["?ticket=".Length..];
            Must(!Encoding.UTF8.GetString(p.Storage.Bytes!).Contains(ticket));
            await p.Authenticate(url);
            string authorized = Encoding.UTF8.GetString(p.Storage.Bytes!);
            Must(!authorized.Contains("synthetic-model-key") && !authorized.Contains("synthetic-human-secret") &&
                !authorized.Contains("accessToken") && !authorized.Contains("ClaimsPrincipal"));
        });
        await check("Private input preserves supplied text in router and immutable persistence", async () =>
        {
            await using PrivateHarness p = new();
            p.Origin.Text = "Analyze my private file using Authorization: Bearer PRIVATE-ACCESS-TOKEN";
            PrivateIngress result = await p.Coordinator.Handle(p.Origin, default);
            Must(result.Handled && p.Store.List().Single().Request == p.Origin.Text &&
                p.Routing.Requests.Single().GetProperty("messages").EnumerateArray().Any(m =>
                    m.GetProperty("role").GetString() == "user" && m.GetProperty("content").GetString()!.Contains(p.Origin.Text)));
        });
        foreach (bool entire in new[] { false, true })
            await check("Private expiry prevents new execution " + (entire ? "whole job" : "approved queue"), async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
                p.H.Clock.Now += TimeSpan.FromMinutes(entire ? 31 : 6);
                await p.Worker.RunOne(default);
                Must(p.Model.Requests.Count == 0 && p.H.Handler.ToolCalls == 0);
                if (entire) Must(p.Store.List().Length == 0);
                else Must(p.Store.Get(job.Id).State == PrivateJobState.AwaitingApproval);
            });
        await check("Private disconnect cancels active human analysis before waiting on its account gate", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
            Task<string>? disconnect = null;
            p.Model.BeforeResponse = () => disconnect = p.Humans.Disconnect(p.H.Policy.Authorize(p.Personal, p.H.Settings));
            await p.Worker.RunOne(default);
            Must(disconnect is not null); await disconnect!;
            Must(p.Store.Get(job.Id).State == PrivateJobState.Cancelled && p.Model.Requests.Count == 1 && p.H.Handler.ToolCalls == 0 &&
                UiText(p.Ui.Wire.Requests.Last()).Contains("cancelled"));
        });
        await check("Private queued job exposes a current cancel action without reauthorizing or replaying", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
            await p.Coordinator.Handle(p.Personal, default);
            JsonElement action = p.Ui.Wire.Requests.Last().Body.GetProperty("attachments")[0].GetProperty("content").GetProperty("actions")[0];
            Must(action.GetProperty("verb").GetString() == "lab13.private.cancel" &&
                p.Store.Get(job.Id).State == PrivateJobState.Ready);
            await p.Coordinator.Action(p.Personal, "lab13.private.cancel", job.Id, action.GetProperty("data").GetProperty("revision").GetInt32(), default);
            Must(!await p.Worker.RunOne(default) && p.Store.Get(job.Id).State == PrivateJobState.Cancelled);
        });
        await check("Private host stop cannot replay analysis or start terminal network delivery", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
            using CancellationTokenSource host = new();
            p.Model.BeforeResponse = host.Cancel;
            await p.Worker.RunOne(host.Token);
            Must(p.Store.Get(job.Id).HasRun && p.Store.Get(job.Id).State != PrivateJobState.Completed &&
                !await p.Worker.RunOne(default) && p.Model.Requests.Count == 1);
            Must(p.Ui.Wire.Requests.All(r => !UiText(r).Contains("AI answer")));
        });
        await check("Private generation mismatch returns to private approval with no AU fallback", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
            p.Store.Change(job.Id, j => j with { HumanGeneration = j.HumanGeneration + 1 });
            await p.Worker.RunOne(default);
            Must(p.Store.Get(job.Id).State == PrivateJobState.AwaitingApproval && p.Model.Requests.Count == 0 &&
                p.H.Handler.Initializes == 0);
        });
        await check("Private bounded store rejects immutable changes and queue overflow", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob first = await p.Initiate();
            await Denied(async () => { p.Store.Change(first.Id, j => j with { Request = "Different authorization" }); await Task.CompletedTask; });
            for (int n = 1; n < 64; n++) p.Store.Add(first with { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) });
            await Denied(async () => { p.Store.Add(first with { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) }); await Task.CompletedTask; });
            Must(p.Store.List().Length == 64 && p.Store.Get(first.Id).Request == first.Request);
        });
        await check("Private storage options reject checkout relative or nonscoped paths without filesystem edits", async () =>
        {
            await using Harness h = new();
            foreach (string path in new[] { "relative", Path.Combine(Environment.CurrentDirectory, "lab13-private-analysis"),
                Path.Combine(Path.GetTempPath(), "not-the-lab-directory") })
                await Denied(async () => { _ = new PrivateAnalysisOptions { StorageDirectory = path }.DirectoryFor(h.Settings); await Task.CompletedTask; });
        });
        await check("Private disabled and slash guarded entry points do not invoke router or proactive messaging", async () =>
        {
            await using PrivateHarness p = new();
            foreach (string command in new[] { "/signin", "/disconnect", "/confirm old", "/file read sample.txt", "help", "status" })
            {
                p.Origin.Text = command;
                Must(!(await p.Coordinator.Handle(p.Origin, default)).Handled);
            }
            p.Options.Enabled = false; p.Origin.Text = "Analyze my private OneDrive";
            Must(!(await p.Coordinator.Handle(p.Origin, default)).Handled && p.Routing.Requests.Count == 0 && p.Ui.Wire.Requests.Count == 0);
        });
        await check("Private production DI registrations resolve human purpose binding and hosted worker without starting either", async () =>
        {
            await using PrivateHarness p = new();
            ServiceCollection services = new();
            services.AddLogging();
            services.AddSingleton(p.Options); services.AddSingleton(p.H.Settings); services.AddSingleton(p.H.Policy);
            services.AddSingleton<TimeProvider>(p.H.Clock); services.AddSingleton(p.ModelOptions);
            services.AddSingleton(p.H.HumanSettings); services.AddSingleton<IHumanTokenCache>(p.H.Cache);
            services.AddSingleton(new WorkIqSessionFactory(tokens => new WorkIqSession(tokens, p.H.Handler)));
            services.AddSingleton<HumanConnections>();
            services.AddSingleton(sp => new WorkIqRouter(p.H.AuSession, sp.GetRequiredService<HumanConnections>(),
                p.H.HumanSettings, p.H.Settings));
            services.AddSingleton(p.Ui.Core); services.AddSingleton(new SetupMode()); services.AddSingleton(new ChannelSetup());
            PrivateAnalysisRegistration.Register(services, p.Options, p.H.Settings);
            services.AddSingleton(p.Store);
            await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
            { ValidateOnBuild = true, ValidateScopes = true });
            Must(provider.GetRequiredService<PrivateAnalysisCoordinator>() is not null &&
                provider.GetServices<IHostedService>().Single() is PrivateAnalysisWorker &&
                provider.GetRequiredService<IPrivateTeams>() is PrivateTeams &&
                p.H.Handler.Initializes == 0 && p.Ui.Wire.Requests.Count == 0);
        });
        await check("Private saved result survives restart only for newly approved delivery not repeated analysis", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
            p.Ui.Wire.Respond = (_, _) => Task.FromResult(p.Ui.Wire.Requests.Last().Body.TryGetProperty("text", out _) ?
                UiTransport.Fail(HttpStatusCode.BadRequest) : UiTransport.Ok());
            await p.Worker.RunOne(default);
            using PrivateJobStore restored = new(new MemoryPrivateStorage { Bytes = p.Storage.Bytes!.ToArray() }, p.H.Clock);
            PrivateAuthorizations auth = new(restored, p.Options, p.H.Policy, p.H.Settings, p.H.HumanSettings, p.ModelOptions, p.H.Clock);
            await using HumanConnections humans = new(p.H.HumanSettings, p.H.Cache,
                new(tokens => new WorkIqSession(tokens, p.H.Handler)), p.H.Clock, auth);
            PrivateAnalysisCoordinator coordinator = new(p.Options, restored, auth, humans, p.H.HumanSettings, p.Teams,
                new(p.ModelOptions, o => new NaturalLanguageModel(o, p.Routing)), p.H.Policy, p.H.Settings, p.H.Clock,
                NullLogger<PrivateAnalysisCoordinator>.Instance);
            using PrivateAnalysisWorker worker = new(restored, auth, humans,
                new(p.H.AuSession, humans, p.H.HumanSettings, p.H.Settings), p.Teams, p.ModelOptions,
                p.H.Policy, p.H.Settings, new(), new(), p.H.Clock, NullLogger<PrivateAnalysisWorker>.Instance,
                _ => throw new Exception("Saved-result recovery must not construct a model."));
            Must(!await worker.RunOne(default) && restored.Get(job.Id).HumanGeneration is null);
            int reads = p.H.Handler.ToolCalls, modelCalls = p.Model.Requests.Count;
            p.Ui.Wire.Respond = (_, _) => Task.FromResult(UiTransport.Ok());
            await coordinator.Handle(p.Personal, default);
            PrivateActionResult consent = await coordinator.Action(p.Personal, "lab13.private.approve", job.Id, restored.Get(job.Id).Revision, default);
            string url = consent.Card!.Value.GetProperty("actions")[0].GetProperty("url").GetString()!;
            await humans.Complete(humans.Begin(new Uri(url).Query["?ticket=".Length..]), Harness.Principal(Harness.Human), default);
            await worker.RunOne(default);
            Must(restored.Get(job.Id).State == PrivateJobState.Completed &&
                p.H.Handler.ToolCalls == reads && p.Model.Requests.Count == modelCalls);
        });
        await check("Private OAuth invalidation after acceptance is surfaced before queued success", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate();
            string url = await p.Approve(job);
            string flow = p.Humans.Begin(new Uri(url).Query["?ticket=".Length..]);
            await using HumanCallbackLease lease = await p.Humans.BeginCallback(flow, default);
            await lease.Complete(Harness.Principal(Harness.Human), default);
            p.Authorization.Cancel(job.Id);
            await Denied(async () => await lease.DisposeAsync());
            Must(p.Store.Get(job.Id).State == PrivateJobState.Cancelled && !await p.Worker.RunOne(default));
        });
        foreach (string fault in new[] { "interrupted", "ownership", "corrupt" })
            await check("Private DPAPI storage fails closed without resetting existing " + fault + " state", async () =>
            {
                if (!OperatingSystem.IsWindows()) return;
                string directory = Path.Combine(Path.GetTempPath(), "lab13-offline-" + Guid.NewGuid().ToString("N"), "lab13-private-analysis");
                string parent = Path.GetDirectoryName(directory)!;
                try
                {
                    using (DpapiPrivateStateStorage disk = new(directory)) disk.Save(Encoding.UTF8.GetBytes("{}"));
                    string file = Path.Combine(directory, fault == "interrupted" ? "jobs.pending" : fault == "ownership" ? "owner.txt" : "jobs.dpapi");
                    File.WriteAllText(file, "synthetic-invalid-data");
                    await Denied(async () =>
                    {
                        using PrivateJobStore invalid = new(new DpapiPrivateStateStorage(directory), TimeProvider.System);
                        await Task.CompletedTask;
                    });
                    Must(File.ReadAllText(file) == "synthetic-invalid-data");
                }
                finally
                {
                    foreach (string file in new[] { "jobs.dpapi", "jobs.pending", "host.lock", "owner.txt" })
                        if (File.Exists(Path.Combine(directory, file))) File.Delete(Path.Combine(directory, file));
                    if (Directory.Exists(directory)) Directory.Delete(directory);
                    if (Directory.Exists(parent)) Directory.Delete(parent);
                }
            });
        await check("Private DPAPI store encrypts persists locks and reloads only its dedicated files", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            string directory = Path.Combine(Path.GetTempPath(), "lab13-offline-" + Guid.NewGuid().ToString("N"), "lab13-private-analysis");
            string parent = Path.GetDirectoryName(directory)!;
            try
            {
                using (DpapiPrivateStateStorage disk = new(directory))
                {
                    disk.Save(Encoding.UTF8.GetBytes("synthetic-private-payload"));
                    Must(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "jobs.dpapi"))).Contains("synthetic-private-payload") &&
                        Encoding.UTF8.GetString(disk.Load()!) == "synthetic-private-payload");
                    bool locked = false;
                    try { using DpapiPrivateStateStorage duplicate = new(directory); } catch (IOException) { locked = true; }
                    Must(locked);
                }
                using DpapiPrivateStateStorage restored = new(directory);
                Must(Encoding.UTF8.GetString(restored.Load()!) == "synthetic-private-payload");
            }
            finally
            {
                foreach (string file in new[] { "jobs.dpapi", "jobs.pending", "host.lock", "owner.txt" })
                    if (File.Exists(Path.Combine(directory, file))) File.Delete(Path.Combine(directory, file));
                if (Directory.Exists(directory)) Directory.Delete(directory);
                if (Directory.Exists(parent)) Directory.Delete(parent);
            }
            await Task.CompletedTask;
        });
    }
}
