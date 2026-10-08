using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Handlers;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static JsonObject PrivateInvoke(PrivateHarness p, PrivateJob job, string verb = "lab13.private.approve")
    {
        JsonObject input = JsonSerializer.SerializeToNode(p.Personal, JsonSerializerOptions.Web)!.AsObject();
        input.Remove("text"); input.Remove("attachments");
        input["type"] = "invoke"; input["name"] = "adaptiveCard/action";
        input["value"] = new JsonObject
        {
            ["trigger"] = "manual",
            ["action"] = new JsonObject { ["type"] = "Action.Execute", ["verb"] = verb,
                ["data"] = new JsonObject { ["job"] = job.Id, ["revision"] = job.Revision } }
        };
        return input;
    }
    private static async Task<(int Status, string? Type, JsonElement Body)> InvokeWire(PrivateHarness p, JsonObject payload,
        CancellationToken host = default, bool legacyError = false, string? authenticatedServiceUrl = null)
    {
        ServiceCollection services = new();
        services.AddLogging(); services.AddSingleton<IConfiguration>(Harness.Config);
        services.AddTeamsBotApplication(); AgentUserTokenProvider.Register(services, Harness.Config);
        services.AddSingleton(p.Ui.Core);
        await using ServiceProvider provider = services.BuildServiceProvider();
        TeamsBotApplication bot = provider.GetRequiredService<TeamsBotApplication>();
        if (legacyError) bot.OnInvoke((_, _) => Task.FromResult<InvokeResponse>(AdaptiveCardResponse.CreateMessageResponse("safe error", 400)));
        else PrivateCardInvoke.Register(bot, () => p.Coordinator, host, p.Log);
        DefaultHttpContext http = new()
        {
            RequestServices = provider,
            // Synthetic authenticated boundary; this does not test or bypass production JWT middleware.
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("serviceurl", authenticatedServiceUrl ?? p.Personal.ServiceUrl!.ToString())], "synthetic"))
        };
        http.Request.Method = "POST"; http.Request.Path = "/api/messages"; http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        http.Response.Body = new MemoryStream();
        IHttpContextAccessor accessor = provider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = http;
        try
        {
            await bot.ProcessAsync(http);
            http.Response.Body.Position = 0;
            using JsonDocument response = await JsonDocument.ParseAsync(http.Response.Body);
            return (http.Response.StatusCode, http.Response.ContentType, response.RootElement.Clone());
        }
        finally { accessor.HttpContext = null; http.Request.Body.Dispose(); http.Response.Body.Dispose(); }
    }
    private static async Task PrivateInvokeWireChecks(Func<string, Func<Task>, Task> check)
    {
        await PrivateBoundInvokeChecks(check);
        foreach (string difference in new[] { "conversation", "type-case", "requester", "personal-thread", "original", "stored", "untrusted-type" })
            await check("Private exact audience denial identifies native policy reference without identifiers " + difference, async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob job = await p.Initiate();
                if (difference == "original")
                {
                    JsonObject origin = JsonNode.Parse(job.Origin.GetRawText())!.AsObject();
                    origin["conversation"]!["id"] = "19:PRIVATE-unapproved-origin";
                    job = p.Store.Add(job with { Id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)),
                        Origin = JsonSerializer.SerializeToElement(origin) });
                }
                if (difference == "stored")
                {
                    JsonObject personal = JsonNode.Parse(job.Personal!.Value.GetRawText())!.AsObject();
                    personal["conversation"]!["id"] = "19:PRIVATE-unapproved-stored";
                    job = p.Store.Change(job.Id, j => j with { Personal = JsonSerializer.SerializeToElement(personal) });
                }
                JsonObject input = PrivateInvoke(p, job);
                if (difference == "conversation") input["conversation"]!["id"] = "19:PRIVATE-unapproved-actual";
                if (difference == "type-case") input["conversation"]!["conversationType"] = "Personal";
                if (difference == "untrusted-type") input["conversation"]!["conversationType"] = "PRIVATE-token-shaped-type";
                const string unapproved = "fadd228d-b16b-4ca1-b197-df52303a4e52";
                if (difference == "requester") input["from"]!["aadObjectId"] = unapproved;
                if (difference == "personal-thread") input["conversation"]!["id"] = p.Personal.Conversation!.Id + ";messageid=PRIVATE";
                var response = await InvokeWire(p, input);
                Must(response.Status == 200 && response.Body.GetProperty("statusCode").GetInt32() == 400);
                string log = p.Log.Captured.Lines.Single(l => l.StartsWith("Private ingress diagnostic:"));
                Must(log.Contains("reason=ContextOrRequesterNotApproved") &&
                    log.Contains("policyReference=" + (difference == "original" ? "OriginalChannel" :
                        difference == "stored" ? "StoredPersonal" : "IncomingAction")), log);
                if (difference is "conversation" or "personal-thread")
                    Must(log.Contains("conversationKnown=false") && log.Contains("chatMatchesJob=false") &&
                        log.Contains("requesterMatchesJob=true"), log);
                if (difference == "requester")
                    Must(log.Contains("conversationKnown=true") && log.Contains("typeMatched=true") &&
                        log.Contains("requesterAllowed=false") && log.Contains("requesterMatchesJob=false") &&
                        log.Contains("chatMatchesJob=true"), log);
                if (difference == "type-case")
                    Must(log.Contains("personalConversationKnown=true") && log.Contains("typeMatched=false") &&
                        log.Contains("actualType=PersonalNonCanonicalCase"), log);
                if (difference == "untrusted-type") Must(log.Contains("actualType=Other"), log);
                if (difference == "personal-thread") Must(log.Contains("threadSuffix=true"), log);
                Must(p.Ui.Wire.Requests.Count == 2 && p.H.Handler.ToolCalls == 0 && p.Store.Get(job.Id).Revision == job.Revision &&
                    !log.Contains("PRIVATE") && !log.Contains(Harness.Human) && !log.Contains(unapproved) &&
                    !log.Contains(job.Id) && !log.Contains(p.Personal.Conversation!.Id), log);
            });
        foreach (bool objectIdAlias in new[] { false, true })
            await check("Private installed SDK native personal invoke retains conversation tenant and requester mapping " + objectIdAlias, async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob job = await p.Initiate();
                JsonObject input = PrivateInvoke(p, job);
                input["conversation"]!["tenantId"] = Harness.Tenant;
                input["conversation"]!["isGroup"] = false;
                input["from"]!["id"] = "29:synthetic-native-teams-user";
                if (objectIdAlias)
                {
                    input["from"]!.AsObject().Remove("aadObjectId");
                    input["from"]!["objectId"] = Harness.Human;
                }
                var response = await InvokeWire(p, input);
                Must(response.Status == 200 && response.Body.GetProperty("statusCode").GetInt32() == 200 &&
                    p.Store.Get(job.Id).State == PrivateJobState.AwaitingSignIn && p.Ui.Wire.Requests.Count == 2 &&
                    !p.Log.Captured.Lines.Any(l => l.Contains("ContextOrRequesterNotApproved")));
                PrivateJob accepted = p.Store.Get(job.Id);
                Must(PrivateContext.Activity(accepted.Personal!.Value).From!.AadObjectId == Harness.Human &&
                    PrivateContext.Activity(accepted.Personal.Value).Conversation!.ConversationType == "personal");
            });
        await check("Private installed SDK old handled-error helper emits invalid outer400 message envelope", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate();
            var wire = await InvokeWire(p, PrivateInvoke(p, job), legacyError: true);
            Must(wire.Status == 400 && wire.Body.GetProperty("statusCode").GetInt32() == 400 &&
                wire.Body.GetProperty("type").GetString() == AdaptiveCardResponseTypes.Message);
        });
        await check("Private installed SDK ProcessAsync returns inline sign-in card HTTP200 without outgoing invoke calls", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate();
            int sends = p.Ui.Wire.Requests.Count;
            p.Ui.Wire.Respond = (_, _) => throw new Exception("An invoke must not call Teams.");
            Stopwatch timer = Stopwatch.StartNew();
            var response = await InvokeWire(p, PrivateInvoke(p, job));
            Must(response.Status == 200 && response.Type!.StartsWith("application/json") &&
                response.Body.GetProperty("statusCode").GetInt32() == 200 &&
                response.Body.GetProperty("type").GetString() == AdaptiveCardResponseTypes.Card, response.Body.GetRawText());
            JsonElement card = response.Body.GetProperty("value");
            Must(card.GetProperty("type").GetString() == "AdaptiveCard" && p.Ui.Wire.Requests.Count == sends &&
                p.H.Handler.ToolCalls == 0 && p.Model.Requests.Count == 0 && timer.Elapsed < TimeSpan.FromSeconds(3));
            string url = card.GetProperty("actions")[0].GetProperty("url").GetString()!;
            Must(p.Store.Get(job.Id).State == PrivateJobState.AwaitingSignIn);
            await p.Authenticate(url);
            Must(p.Store.Get(job.Id).State == PrivateJobState.Ready);
            Must(p.Log.Captured.Lines.Any(l => l.Contains("event=Arrived; action=Approve")) &&
                p.Log.Captured.Lines.Any(l => l.Contains("http=200; inner=200")) &&
                p.Log.Captured.Lines.All(l => !l.Contains(job.Id) && !l.Contains(url) && !l.Contains(Harness.Human)));
        });
        foreach (string mode in new[] { "wrong-human", "missing-tenant", "missing-au", "channel", "expired", "revision",
            "automatic", "wrong-action", "malformed", "cancel", "host-cancel" })
            await check("Private real SDK invoke denial cancel envelope preserves authority " + mode, async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob job = await p.Initiate();
                JsonObject input = PrivateInvoke(p, job, mode == "cancel" ? "lab13.private.cancel" : "lab13.private.approve");
                if (mode == "wrong-human") input["from"]!["aadObjectId"] = Harness.Other;
                if (mode == "missing-tenant") input["channelData"] = null;
                if (mode == "missing-au") input["recipient"]!["agenticAppId"] = null;
                if (mode == "channel") input["conversation"]!["conversationType"] = "channel";
                if (mode == "expired") p.H.Clock.Now += TimeSpan.FromMinutes(31);
                if (mode == "revision") input["value"]!["action"]!["data"]!["revision"] = 999;
                if (mode == "automatic") input["value"]!["trigger"] = "automatic";
                if (mode == "wrong-action") input["value"]!["action"]!["verb"] = "PRIVATE-untrusted-action";
                if (mode == "malformed") input["value"] = "PRIVATE-not-a-value-object";
                using CancellationTokenSource host = new();
                if (mode == "host-cancel") host.Cancel();
                var result = await InvokeWire(p, input, host.Token);
                Must(result.Status == 200 && result.Body.GetProperty("statusCode").GetInt32() == (mode == "cancel" ? 200 : 400) &&
                    result.Body.GetProperty("type").GetString() == (mode == "cancel" ? AdaptiveCardResponseTypes.Message : "application/vnd.microsoft.error"),
                    result.Body.GetRawText());
                Must(p.Ui.Wire.Requests.Count == 2 && p.Model.Requests.Count == 0 && p.H.Handler.ToolCalls == 0);
                if (mode != "expired") Must(p.Store.Get(job.Id).State == (mode == "cancel" ? PrivateJobState.Cancelled : PrivateJobState.AwaitingApproval));
                Must(p.Log.Captured.Lines.All(l => !l.Contains("PRIVATE") && !l.Contains(job.Id) && !l.Contains(Harness.Human)));
            });
        await check("Private duplicate invoke reuses only live same-purpose link and never reissues redeemed link", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate();
            JsonObject invoke = PrivateInvoke(p, job);
            var first = await InvokeWire(p, invoke);
            var retry = await InvokeWire(p, invoke);
            string url = first.Body.GetProperty("value").GetProperty("actions")[0].GetProperty("url").GetString()!;
            Must(retry.Body.GetProperty("value").GetProperty("actions")[0].GetProperty("url").GetString() == url &&
                p.Store.Get(job.Id).Revision == job.Revision + 1 && p.Ui.Wire.Requests.Count == 2);
            string flow = p.Humans.Begin(new Uri(url).Query["?ticket=".Length..]);
            var inProgress = await InvokeWire(p, invoke);
            Must(inProgress.Body.GetProperty("type").GetString() == AdaptiveCardResponseTypes.Message &&
                !inProgress.Body.GetRawText().Contains("ticket="));
            p.Humans.Abandon(flow);
            var abandoned = await InvokeWire(p, invoke);
            Must(abandoned.Body.GetProperty("type").GetString() == AdaptiveCardResponseTypes.Message &&
                abandoned.Body.GetProperty("value").GetString()!.Contains("already used") &&
                !abandoned.Body.GetRawText().Contains("ticket=") && p.Model.Requests.Count == 0);
        });
        await check("Private connected invoke and duplicate acknowledgment queue one execution without model before response", async () =>
        {
            await using PrivateHarness p = new();
            var invocation = p.H.Policy.Authorize(p.Personal, p.H.Settings);
            string prompt = await p.Humans.CreateLink(invocation, default);
            await p.Authenticate(BrowserSignIn.PromptUrl(prompt, p.H.HumanSettings)!);
            PrivateJob job = await p.Initiate();
            JsonObject invoke = PrivateInvoke(p, job);
            var first = await InvokeWire(p, invoke);
            var retry = await InvokeWire(p, invoke);
            Must(first.Status == 200 && retry.Status == 200 && p.Store.Get(job.Id).State == PrivateJobState.Ready &&
                p.Model.Requests.Count == 0 && p.Ui.Wire.Requests.Count == 2);
            await p.Worker.RunOne(default);
            Must(!await p.Worker.RunOne(default) && p.Model.Requests.Count == 2);
        });
        foreach (string change in new[] { "cancel", "expire", "disconnect", "wrong-human", "wrong-tenant", "wrong-chat", "wrong-au", "reapproval" })
            await check("Private repeated card response never exposes link after authorization change " + change, async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob job = await p.Initiate();
                JsonObject invoke = PrivateInvoke(p, job);
                var first = await InvokeWire(p, invoke);
                Must(first.Body.GetProperty("type").GetString() == AdaptiveCardResponseTypes.Card);
                if (change == "cancel") p.Authorization.Cancel(job.Id);
                if (change == "expire") p.H.Clock.Now += TimeSpan.FromMinutes(6);
                if (change == "disconnect") await p.Humans.Disconnect(p.H.Policy.Authorize(p.Personal, p.H.Settings));
                if (change == "wrong-human") invoke["from"]!["aadObjectId"] = Harness.Other;
                if (change == "wrong-tenant") invoke["channelData"]!["tenant"]!["id"] = Harness.Other;
                if (change == "wrong-chat") invoke["conversation"]!["id"] = "19:another";
                if (change == "wrong-au") invoke["recipient"]!["agenticUserId"] = Harness.Other;
                if (change == "reapproval") await p.Coordinator.Handle(p.Personal, default);
                var repeated = await InvokeWire(p, invoke);
                Must(repeated.Status == 200 && repeated.Body.GetProperty("statusCode").GetInt32() == 400 &&
                    !repeated.Body.GetRawText().Contains("ticket=") && p.Model.Requests.Count == 0);
            });
        await check("Private invoke account-gate timeout responds without consuming consent or acquiring tokens", async () =>
        {
            await using PrivateHarness p = new();
            var invocation = p.H.Policy.Authorize(p.Personal, p.H.Settings);
            await p.Authenticate(BrowserSignIn.PromptUrl(await p.Humans.CreateLink(invocation, default), p.H.HumanSettings)!);
            PrivateJob job = await p.Initiate();
            await using HumanLease held = await p.Humans.Acquire(invocation, default);
            Stopwatch time = Stopwatch.StartNew();
            var response = await InvokeWire(p, PrivateInvoke(p, job));
            Must(response.Status == 200 && response.Body.GetProperty("statusCode").GetInt32() == 400 &&
                p.Store.Get(job.Id).State == PrivateJobState.AwaitingApproval &&
                p.Store.Get(job.Id).Revision == job.Revision && time.Elapsed < TimeSpan.FromSeconds(5) &&
                p.H.Handler.ToolCalls == 0 && p.Ui.Wire.Requests.Count == 2);
            Must(p.Log.Captured.Lines.Any(l => l.Contains("stage=InvokeHumanGate") && l.Contains("kind=Cancelled")));
        });
    }
}
