using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Teams.Apps.Schema;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private sealed class PrivateLog<T> : ILogger<T>
    {
        internal readonly UiLog Captured = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) =>
            Captured.Log(level, id, state, error, format);
    }
    private static async Task PrivateIngressDiagnosticChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (string state in new[] { "none", "expired", "other-owner", "terminal" })
            await check("Private empty recovery evidence distinguishes only own unexpired terminal state " + state, async () =>
            {
                await using PrivateHarness p = new();
                if (state != "none")
                {
                    PrivateJob job = await p.Initiate();
                    if (state == "expired") p.H.Clock.Now += TimeSpan.FromMinutes(31);
                    if (state == "terminal") p.Authorization.Cancel(job.Id);
                    if (state == "other-owner")
                    {
                        p.Personal.From!.AadObjectId = Harness.Other;
                        p.H.Policy.Contexts.Single(c => c.Type == "personal").RequesterIds = [Harness.Human, Harness.Other];
                    }
                }
                using PrivateIngressDiagnostic diagnostic = new();
                PrivateIngress result = await p.Coordinator.Handle(p.Personal, default);
                Must(result.Reply == "No unexpired private requests. Interrupted/completed requests never re-run automatically.");
                string log = p.Log.Captured.Lines.Last();
                Must(log.StartsWith("Private recovery lookup:") && log.Contains(state == "terminal" ?
                    "outcome=OnlyMatchingTerminalRecords" : "outcome=NoMatchingUnexpiredRecord") &&
                    !log.Contains("personal.txt") && !log.Contains(Harness.Human) && !log.Contains(Harness.Other), log);
            });
        foreach (bool recognized in new[] { true, false })
            await check("Private routing HTTP evidence reuses provider allowlist without copying error text " + recognized, async () =>
            {
                ModelHandler model = new(new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["code"] = recognized ? "unsupported_value" : "PRIVATE-provider-code",
                        ["type"] = recognized ? "invalid_request_error" : "PRIVATE-provider-type",
                        ["param"] = recognized ? "tool_choice" : "PRIVATE-field",
                        ["message"] = "PRIVATE-request-file human@example.invalid Authorization: PRIVATE https://secret.invalid/?code=PRIVATE"
                    }
                }) { Status = HttpStatusCode.BadRequest };
                await using PrivateHarness p = new(routing: model);
                using PrivateIngressDiagnostic diagnostic = new();
                try { await p.Coordinator.Handle(p.Origin, default); }
                catch (Exception error) when (PrivateAnalysisCoordinator.SafeFailure(error))
                { diagnostic.Log(p.Log, error, routeFallback: false); }
                string log = p.Log.Captured.Lines.Single();
                Must(log.Contains("stage=RouterRequest") && log.Contains("http=400") &&
                    log.Contains(recognized ? "modelCode=unsupported_value" : "modelCode=withheld-or-unknown") &&
                    log.Contains(recognized ? "modelParameter=tool_choice" : "modelParameter=withheld-or-unknown") &&
                    !log.Contains("PRIVATE") && !log.Contains("example.invalid") && !log.Contains("secret.invalid") &&
                    p.Store.List().Length == 0 && p.Ui.Wire.Requests.Count == 0, log);
            });
        foreach (string failure in new[] { "context", "sender", "router-choice", "router-http", "persist" })
            await check("Private outer ingress evidence distinguishes pre-proactive failure " + failure, async () =>
            {
                await using PrivateHarness p = new(routing: failure == "router-choice" ? new(Completion("PRIVATE-ROUTER-TEXT")) : null);
                using PrivateIngressDiagnostic diagnostic = new();
                if (failure == "context") p.H.Policy.Contexts = [];
                if (failure == "sender") p.Origin.Recipient!.AgenticAppId = null;
                if (failure == "router-http") p.Routing.Status = HttpStatusCode.BadRequest;
                if (failure == "persist") p.Storage.Fail = true;
                Exception? observed = null;
                try { await p.Coordinator.Handle(p.Origin, default); }
                catch (Exception error) when (PrivateAnalysisCoordinator.SafeFailure(error))
                { observed = error; diagnostic.Log(p.Log, error, routeFallback: false); }
                Must(observed is not null && p.Ui.Wire.Requests.Count == 0 && p.H.Handler.ToolCalls == 0);
                string log = p.Log.Captured.Lines.Single();
                PrivateIngressStage expected = failure switch
                {
                    "context" => PrivateIngressStage.ContextAuthorization, "sender" => PrivateIngressStage.SenderValidation,
                    "router-choice" => PrivateIngressStage.RouterChoice, "router-http" => PrivateIngressStage.RouterRequest,
                    _ => PrivateIngressStage.JobPersist
                };
                Must(log.Contains("boundary=OuterFailure") && log.Contains("stage=" + expected) &&
                    diagnostic.Persistence == (failure == "persist" ? PrivatePersistence.Unconfirmed : PrivatePersistence.NotAttempted), log);
                if (failure == "router-http") Must(log.Contains("http=400"), log);
                Must(!log.Contains("personal.txt") && !log.Contains("PRIVATE") && !log.Contains(Harness.Human) &&
                    !log.Contains("synthetic-model-key") && !log.Contains("http://") && !log.Contains("https://"));
            });
        foreach (string failure in new[] { "403", "io", "policy", "service", "id", "send", "send-ack" })
            await check("Private proactive evidence distinguishes create response policy and card send " + failure, async () =>
            {
                await using PrivateHarness p = new();
                using PrivateIngressDiagnostic diagnostic = new();
                p.Ui.Wire.Respond = (request, _) =>
                {
                    bool create = request.RequestUri!.AbsolutePath == "/v3/conversations";
                    if (create && failure == "io") throw new IOException("PRIVATE-BODY-https://secret.invalid/?token=PRIVATE");
                    return Task.FromResult(create ? failure switch
                    {
                        "403" => UiTransport.Fail(HttpStatusCode.Forbidden),
                        "policy" => UiTransport.Ok("19:not-approved"),
                        "service" => new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("""{"id":"19:personal","serviceUrl":"https://different.invalid/"}""") },
                        "id" => UiTransport.Ok(""),
                        _ => UiTransport.Ok("19:personal")
                    } : failure == "send" ? UiTransport.Fail(HttpStatusCode.TooManyRequests) : UiTransport.Ok(""));
                };
                PrivateIngress result = await p.Coordinator.Handle(p.Origin, default);
                Must(result.Reply == PrivateAnalysisCoordinator.Neutral &&
                    diagnostic.Persistence == PrivatePersistence.Confirmed && diagnostic.RouteSource == PrivateRouteSource.Create &&
                    p.Store.List().Length == 1 && p.Model.Requests.Count == 0);
                string log = p.Log.Captured.Lines.Single(l => l.StartsWith("Private ingress diagnostic:"));
                string stage = failure switch
                { "403" or "io" => "PersonalCreate", "service" or "id" => "PersonalCreateResponse",
                    "policy" => "PersonalPolicy", _ => "ApprovalSend" };
                Must(log.Contains("boundary=RouteFallback") && log.Contains("stage=" + stage), log);
                if (failure == "403") Must(log.Contains("http=403"), log);
                if (failure == "send") Must(log.Contains("http=429"), log);
                if (failure == "policy") Must(log.Contains("reason=ContextOrRequesterNotApproved"), log);
                Must(!log.Contains("PRIVATE") && !log.Contains("different.invalid") && !log.Contains(Harness.Human) &&
                    !log.Contains(p.Store.List().Single().Id));
                Must(p.Ui.Wire.Requests.Count == (failure is "send" or "send-ack" ? 2 : 1));
            });
        await check("Private remembered-route diagnostics do not create a new conversation or expand revoked policy", async () =>
        {
            await using PrivateHarness p = new();
            await p.Initiate();
            p.Origin.Id = "new-original-message";
            p.H.Policy.Contexts = p.H.Policy.Contexts.Where(c => c.Type != "personal").ToArray();
            // The router is independently called for this new request.
            ModelHandler routes = new(Completion(tool: "request_human_analysis", args: new { }));
            PrivateAnalysisCoordinator coordinator = new(p.Options, p.Store, p.Authorization, p.Humans, p.H.HumanSettings,
                p.Teams, new(p.ModelOptions, o => new NaturalLanguageModel(o, routes)), p.H.Policy, p.H.Settings, p.H.Clock, p.Log);
            using PrivateIngressDiagnostic diagnostic = new();
            PrivateIngress result = await coordinator.Handle(p.Origin, default);
            Must(result.Reply == PrivateAnalysisCoordinator.Neutral && p.Ui.Wire.Requests.Count == 2 &&
                diagnostic.RouteSource == PrivateRouteSource.Remembered && diagnostic.Stage == PrivateIngressStage.PersonalBinding);
            Must(p.Log.Captured.Lines.Last().Contains("reason=ContextOrRequesterNotApproved"));
        });
        await check("Private approved Teams channel alias survives durable capture and automatic private approval delivery", async () =>
        {
            await using PrivateHarness p = new();
            JsonObject node = JsonSerializer.SerializeToNode(p.Origin, JsonSerializerOptions.Web)!.AsObject();
            node["channelData"]!["teamsChannelId"] = Harness.Channel;
            node["channelData"]!.AsObject().Remove("channel");
            MessageActivity alias = node.Deserialize<MessageActivity>(JsonSerializerOptions.Web)!;
            using PrivateIngressDiagnostic diagnostic = new();
            PrivateIngress result = await p.Coordinator.Handle(alias, default);
            Must(result.Reply == PrivateAnalysisCoordinator.CardSent && p.Ui.Wire.Requests.Count == 2 &&
                p.Store.List().Single().MessageId is not null && p.Log.Captured.Lines.Count == 0);
            PrivateJob job = p.Store.List().Single();
            Must(PrivateContext.Activity(job.Origin).ChannelData!.Channel!.Id == Harness.Channel);
            await p.Authenticate(await p.Approve(job));
            await p.Worker.RunOne(default);
            Must(p.Store.Get(job.Id).State == PrivateJobState.Completed && p.H.Handler.Mutations == 0);
        });
        await check("Private diagnostic payloads with dynamic exceptions never leak raw details or claim unknown HTTP", () =>
        {
            using PrivateIngressDiagnostic diagnostic = new();
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.ChannelReply);
            UiLog log = new();
            diagnostic.Log(log, new LabException("Authorization: PRIVATE-SECRET https://secret.invalid/?code=PRIVATE"), false);
            diagnostic.Log(log, new OperationCanceledException("PRIVATE", new HttpRequestException("PRIVATE", null, HttpStatusCode.Forbidden)), false);
            Must(log.Lines[0].Contains("http=unavailable") && log.Lines[0].Contains("reason=ValidationOther") &&
                log.Lines[1].Contains("kind=Cancelled") && log.Lines[1].Contains("http=403") &&
                log.Lines.All(line => !line.Contains("PRIVATE") && !line.Contains("secret.invalid")));
            return Task.CompletedTask;
        });
        await check("Private diagnostic scopes reset stage HTTP evidence and restore isolated outer scope", () =>
        {
            using PrivateIngressDiagnostic outer = new();
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.RouterRequest);
            PrivateIngressDiagnostic.ObserveHttp(HttpStatusCode.BadRequest);
            using (PrivateIngressDiagnostic inner = new())
            {
                Must(inner.Correlation != outer.Correlation && inner.HttpStatus is null);
                PrivateIngressDiagnostic.Enter(PrivateIngressStage.PersonalCreate);
                PrivateIngressDiagnostic.ObserveHttp(HttpStatusCode.Forbidden);
            }
            Must(PrivateIngressDiagnostic.Current == outer && outer.HttpStatus == 400);
            PrivateIngressDiagnostic.Enter(PrivateIngressStage.ChannelReply);
            Must(outer.HttpStatus is null);
            return Task.CompletedTask;
        });
    }
}
