using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Api.Clients;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Core;
using Microsoft.Teams.Core.Http;
using Microsoft.Teams.Core.Schema;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static MessageActivity ProgressActivity(string text = "Read this team", string type = "channel")
    {
        MessageActivity activity = AttachedActivity(text, [], type);
        activity.ServiceUrl = new("https://teams.example.invalid/");
        return activity;
    }

    private sealed class UiLog : ILogger
    {
        internal readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> format)
        { Must(error is null); Lines.Add(format(state, error)); }
    }

    private sealed record UiRequest(HttpMethod Method, Uri Url, JsonElement Body, AgenticIdentity? Identity, bool Marked);
    private sealed class UiTransport : HttpMessageHandler
    {
        internal readonly List<UiRequest> Requests = [];
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond;
        internal static HttpResponseMessage Ok(string id = "own-outgoing-999") =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id }), Encoding.UTF8, "application/json") };
        internal static HttpResponseMessage Fail(HttpStatusCode status, RetryConditionHeaderValue? retry = null)
        {
            HttpResponseMessage response = new(status) { Content = new StringContent("PRIVATE-ERROR-BODY") };
            response.Headers.RetryAfter = retry;
            return response;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            request.Options.TryGetValue(new HttpRequestOptionsKey<AgenticIdentity>(BotRequestContext.AgenticIdentityKey), out var identity);
            using JsonDocument doc = JsonDocument.Parse(request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct));
            Requests.Add(new(request.Method, request.RequestUri!, doc.RootElement.Clone(), identity, ProgressDeliveryScope.Active));
            return Respond is null ? Ok() : await Respond(request, ct);
        }
    }

    private sealed class UiFixture : IDisposable
    {
        internal readonly UiTransport Wire = new();
        internal readonly UiLog Log = new();
        internal readonly ActivityClient Client;
        internal readonly ConversationApiClient Conversations;
        internal readonly ConversationClient Core;
        internal readonly DirectProgress Progress;
        private readonly HttpClient http;
        internal UiFixture(Harness h, MessageActivity activity, TimeProvider? clock = null, CancellationToken host = default)
        {
            http = new(new ProgressDeliveryHandler(clock ?? TimeProvider.System)
            { InnerHandler = Wire });
            ConversationClient core = Core = new(http, NullLogger<ConversationClient>.Instance);
            // Public methods on the installed SDK; its internal constructor is only used by this offline fixture.
            Client = (ActivityClient)Activator.CreateInstance(typeof(ActivityClient),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [activity.ServiceUrl!, core], null)!;
            Conversations = (ConversationApiClient)Activator.CreateInstance(typeof(ConversationApiClient),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [activity.ServiceUrl!, core], null)!;
            Progress = new(activity, h.Settings, h.Policy,
                async (message, ct) => (await Client.CreateAsync(message.Conversation!.Id, message, cancellationToken: ct))?.Id,
                async (id, message, ct) => { await Client.UpdateAsync(message.Conversation!.Id, id, message, cancellationToken: ct); },
                host, Log, clock);
        }
        public void Dispose() { Progress.Dispose(); http.Dispose(); }
    }

    private sealed class ObservedProgress(DirectProgress inner, TimingClock clock, Action<DirectStage, int> observe) : IDirectProgress
    {
        public Task StartAsync(CancellationToken ct) => inner.StartAsync(ct);
        public Task ReportAsync(DirectStage stage, int number, CancellationToken ct)
        {
            observe(stage, number);
            clock.Advance(2100);
            return inner.ReportAsync(stage, number, ct);
        }
    }

    private static string UiText(UiRequest request) => request.Body.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
    private static async Task DirectProgressChecks(Func<string, Func<Task>, Task> check)
    {
        await check("Direct Teams SDK named conversation client matches safe delivery registration", () =>
        {
            Must((string?)typeof(ConversationClient).GetField("ConversationHttpClientName", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetRawConstantValue() == ProgressDeliveryHandler.ClientName);
            Must(!ProgressDeliveryScope.Active);
            return Task.CompletedTask;
        });
        await check("Direct safe response handler extends actual named SDK pipeline without replacing AU auth", () =>
        {
            ServiceCollection services = new();
            IConfiguration config = Harness.Config;
            services.AddLogging(); services.AddSingleton(config);
            services.AddTeamsBotApplication();
            AgentUserTokenProvider.Register(services, config);
            services.AddHttpClient(ProgressDeliveryHandler.ClientName)
                .AddHttpMessageHandler(() => new ProgressDeliveryHandler(TimeProvider.System))
                .ConfigurePrimaryHttpMessageHandler(() => new UiTransport());
            using ServiceProvider provider = services.BuildServiceProvider();
            HttpMessageHandler pipeline = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ProgressDeliveryHandler.ClientName);
            List<Type> types = [];
            for (HttpMessageHandler? handler = pipeline; handler is not null; handler = (handler as DelegatingHandler)?.InnerHandler)
                types.Add(handler.GetType());
            Must(types.Contains(typeof(ProgressDeliveryHandler)) &&
                types.Any(t => t.FullName == "Microsoft.Teams.Core.Hosting.BotAuthenticationHandler") &&
                types.Last() == typeof(UiTransport), string.Join(",", types.Select(t => t.Name)));
            return Task.CompletedTask;
        });
        await check("Direct Working card actual SDK POST and AU authenticated PUT replace own ID in same thread", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            MessageActivity activity = ProgressActivity();
            using UiFixture ui = new(h, activity);
            ModelHandler model = new(Completion("Complete <safe> answer."));
            string? fallback = await ui.Progress.RunAsync(() => App(h, model, DirectOptions()).Handle(activity, default, ui.Progress));
            Must(fallback is null && ui.Progress.Delivered && ui.Wire.Requests.Count == 2);
            UiRequest first = ui.Wire.Requests[0], last = ui.Wire.Requests[1];
            Must(first.Method == HttpMethod.Post && UiText(first) == "" &&
                first.Body.GetProperty("attachments").GetArrayLength() == 1 &&
                first.Body.GetProperty("attachments")[0].GetProperty("contentType").GetString() == "application/vnd.microsoft.card.adaptive");
            Must(last.Method == HttpMethod.Put && last.Url.AbsolutePath.EndsWith("/activities/own-outgoing-999") &&
                last.Body.GetProperty("id").GetString() == "own-outgoing-999" &&
                UiText(last).Contains("Complete &lt;safe&gt; answer.") && UiText(last).Contains("LLM tool-selection") &&
                !last.Body.TryGetProperty("attachments", out _), last.Body.GetRawText());
            foreach (UiRequest r in ui.Wire.Requests)
            {
                Must(r.Marked && r.Identity?.AgenticAppId == Harness.Agent && r.Identity.AgenticUserId == Harness.Au &&
                    r.Body.GetProperty("from").GetProperty("agenticUserId").GetString() == Harness.Au &&
                    r.Body.GetProperty("from").GetProperty("agenticAppId").GetString() == Harness.Agent &&
                    r.Body.GetProperty("conversation").GetProperty("id").GetString() == activity.Conversation!.Id &&
                    r.Body.GetProperty("replyToId").GetString() == activity.ReplyToId &&
                    r.Url.Host == "teams.example.invalid" &&
                    Uri.UnescapeDataString(r.Url.AbsolutePath).Contains(activity.Conversation.Id));
            }
            Must(ui.Progress.ActivityId != activity.Id && ui.Progress.ActivityId != activity.ReplyToId && !ProgressDeliveryScope.Active);
        });
        await check("Direct progress hooks correspond to actual catalog model and selected tool stages", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            TimingClock clock = new();
            MessageActivity activity = ProgressActivity();
            using UiFixture ui = new(h, activity, clock);
            ModelHandler model = new(Completion(tool: "fetch", args: Fetch(Team)), Completion("Observed team."));
            List<DirectStage> stages = [];
            ObservedProgress events = new(ui.Progress, clock, (stage, n) =>
            {
                stages.Add(stage);
                if (stage == DirectStage.Catalog) Must(model.Requests.Count == 0 && h.Handler.ToolCalls == 0);
                if (stage == DirectStage.Model) Must(model.Requests.Count == n - 1);
                if (stage == DirectStage.Tool) Must(model.Requests.Count == 1 && h.Handler.ToolCalls == 0 && n == 1);
                if (stage == DirectStage.ToolReturned) Must(h.Handler.ToolCalls == 1 && n == 1);
            });
            await ui.Progress.RunAsync(() => App(h, model, DirectOptions(), clock).Handle(activity, default, events));
            Must(stages.SequenceEqual([DirectStage.Catalog, DirectStage.Model, DirectStage.Tool, DirectStage.ToolReturned, DirectStage.Model]));
            Must(ui.Wire.Requests.Count == 7 && UiText(ui.Wire.Requests.Last()).Contains("Observed team.") &&
                UiText(ui.Wire.Requests.Last()).Contains("includes awaited progress delivery"));
            foreach (UiRequest r in ui.Wire.Requests.SkipLast(1))
                Must(!r.Body.GetRawText().Contains(Team) && !r.Body.GetRawText().Contains("entityUrls"));
        });
        await check("Direct progress coalesces rapid stages and terminal replacement bypasses pace", async () =>
        {
            await using Harness h = new(); TimingClock clock = new();
            using UiFixture ui = new(h, ProgressActivity(), clock);
            await ui.Progress.StartAsync(default);
            await ui.Progress.ReportAsync(DirectStage.Catalog, 0, default);
            clock.Advance(1999);
            await ui.Progress.ReportAsync(DirectStage.Model, 1, default);
            Must(ui.Wire.Requests.Count == 1);
            clock.Advance(1);
            await ui.Progress.ReportAsync(DirectStage.Tool, 1, default);
            Must(ui.Wire.Requests.Count == 2);
            await ui.Progress.CompleteAsync("final");
            clock.Advance(3000);
            await ui.Progress.ReportAsync(DirectStage.Model, 2, default);
            await ui.Progress.CompleteAsync("stale final");
            Must(ui.Wire.Requests.Count == 3 && UiText(ui.Wire.Requests.Last()) == BotText.Html("final"));
        });
        await check("Direct final serializes behind pending update and stale stage cannot overwrite final", async () =>
        {
            await using Harness h = new(); TimingClock clock = new();
            using UiFixture ui = new(h, ProgressActivity(), clock);
            await ui.Progress.StartAsync(default);
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Wire.Respond = async (_, ct) =>
            {
                if (ui.Wire.Requests.Count == 2) { entered.SetResult(); await release.Task.WaitAsync(ct); }
                return UiTransport.Ok();
            };
            clock.Advance(2000);
            Task stage = ui.Progress.ReportAsync(DirectStage.Model, 1, default);
            await entered.Task;
            Task final = ui.Progress.CompleteAsync("terminal");
            Must(!final.IsCompleted && ui.Wire.Requests.Count == 2);
            release.SetResult(); await stage; await final;
            clock.Advance(3000); await ui.Progress.ReportAsync(DirectStage.Tool, 9, default);
            Must(ui.Wire.Requests.Count == 3 && UiText(ui.Wire.Requests.Last()) == BotText.Html("terminal"));
        });
        foreach (string failure in new[] { "no-id", "empty-id", "http429", "http503", "io", "cancel" })
            await check("Direct initial delivery stops before catalog model tools without resend " + failure, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                MessageActivity activity = ProgressActivity();
                using UiFixture ui = new(h, activity);
                ui.Wire.Respond = (_, _) => failure switch
                {
                    "no-id" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }),
                    "empty-id" => Task.FromResult(UiTransport.Ok(" ")),
                    "http429" => Task.FromResult(UiTransport.Fail(HttpStatusCode.TooManyRequests, new(TimeSpan.Zero))),
                    "http503" => Task.FromResult(UiTransport.Fail(HttpStatusCode.ServiceUnavailable)),
                    "io" => throw new IOException("PRIVATE-IO"),
                    _ => throw new OperationCanceledException("PRIVATE-CANCEL")
                };
                ModelHandler model = new(Completion("must not run"));
                string? fallback = await ui.Progress.RunAsync(() => App(h, model, DirectOptions()).Handle(activity, default, ui.Progress));
                Must(fallback is null && ui.Progress.Attempted && !ui.Progress.Delivered && ui.Progress.ActivityId is null &&
                    ui.Wire.Requests.Count == 1 && model.Requests.Count == 0 && h.Handler.Initializes == 0 && h.Handler.ToolCalls == 0);
                Must(ui.Log.Lines.Count > 0 && !string.Join("", ui.Log.Lines).Contains("PRIVATE"));
            });
        foreach (string failure in new[] { "model400", "cancel", "ambiguous-write" })
            await check("Direct terminal error or cancellation replaces own card without replay " + failure, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                using CancellationTokenSource cancel = new();
                MessageActivity activity = ProgressActivity("Perform my operation");
                using UiFixture ui = new(h, activity);
                ModelHandler model = new(Completion(tool: "create_entity", args: CreateArgs()));
                if (failure == "model400") model.Status = HttpStatusCode.BadRequest;
                if (failure == "cancel") model.BeforeResponse = cancel.Cancel;
                if (failure == "ambiguous-write") h.Handler.MutationTimeout = true;
                await ui.Progress.RunAsync(() => App(h, model, DirectOptions()).Handle(activity, cancel.Token, ui.Progress));
                string text = UiText(ui.Wire.Requests.Last());
                Must(ui.Progress.Delivered && ui.Wire.Requests.Count(r => r.Method == HttpMethod.Post) == 1 &&
                    ui.Wire.Requests.Last().Method == HttpMethod.Put && model.Requests.Count == 1 &&
                    h.Handler.Mutations == (failure == "ambiguous-write" ? 1 : 0), text);
                Must(text.Contains(failure == "model400" ? "HTTP" : failure == "cancel" ? "cancelled" : "UNKNOWN OUTCOME"), text);
            });
        await check("Direct unexpected processing failure gets safe terminal UI and propagates", async () =>
        {
            await using Harness h = new(); using UiFixture ui = new(h, ProgressActivity());
            bool thrown = false;
            try
            {
                await ui.Progress.RunAsync(async () => { await ui.Progress.StartAsync(default); throw new ApplicationException("PRIVATE"); });
            }
            catch (ApplicationException) { thrown = true; }
            Must(thrown && ui.Progress.Delivered && UiText(ui.Wire.Requests.Last()).Contains("stopped without a final answer") &&
                !UiText(ui.Wire.Requests.Last()).Contains("PRIVATE"));
        });
        foreach (string failure in new[] { "http400", "http503", "io", "long429" })
            await check("Direct failed final delivery never duplicates message or successful writes " + failure, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                using CancellationTokenSource host = new();
                MessageActivity activity = ProgressActivity("Create two things");
                using UiFixture ui = new(h, activity, host: host.Token);
                ui.Wire.Respond = (request, _) =>
                {
                    if (request.Method == HttpMethod.Post) return Task.FromResult(UiTransport.Ok());
                    if (failure == "io") throw new IOException("PRIVATE");
                    if (failure == "long429")
                    {
                        host.Cancel();
                        return Task.FromResult(UiTransport.Fail(HttpStatusCode.TooManyRequests, new(TimeSpan.FromHours(1))));
                    }
                    return Task.FromResult(UiTransport.Fail(failure == "http400" ? HttpStatusCode.BadRequest : HttpStatusCode.ServiceUnavailable,
                        new(TimeSpan.Zero)));
                };
                ModelHandler model = new(TwoWrites(), Completion("Two writes returned."));
                string? fallback = await ui.Progress.RunAsync(() => App(h, model, DirectOptions()).Handle(activity, default, ui.Progress));
                Must(fallback is null && !ui.Progress.Delivered && ui.Progress.DeliveryState == "terminal-delivery-unconfirmed" &&
                    ui.Wire.Requests.Count(r => r.Method == HttpMethod.Post) == 1 &&
                    ui.Wire.Requests.Count(r => r.Method == HttpMethod.Put) == (failure == "http503" ? 3 : 1) &&
                    h.Handler.Mutations == 2 && model.Requests.Count == 2);
                Must(ui.Log.Lines.Count > 0 && !string.Join("", ui.Log.Lines).Contains("PRIVATE"));
            });
        await check("Direct failed interim update pauses UI only then final targets same ID", async () =>
        {
            await using Harness h = new(); TimingClock clock = new();
            using UiFixture ui = new(h, ProgressActivity(), clock);
            await ui.Progress.StartAsync(default);
            ui.Wire.Respond = (_, _) => Task.FromResult(ui.Wire.Requests.Count == 2 ?
                UiTransport.Fail(HttpStatusCode.BadRequest) : UiTransport.Ok());
            clock.Advance(3000); await ui.Progress.ReportAsync(DirectStage.Tool, 1, default);
            clock.Advance(3000); await ui.Progress.ReportAsync(DirectStage.Model, 2, default);
            Must(ui.Wire.Requests.Count == 2 && ui.Progress.DeliveryState == "progress-updates-paused");
            await ui.Progress.CompleteAsync("completed despite UI failure");
            Must(ui.Progress.Delivered && ui.Wire.Requests.Count == 3);
        });
        await check("Direct cancelled interim 429 retains Retry-After before terminal attempt", async () =>
        {
            await using Harness h = new(); TimingClock clock = new();
            using CancellationTokenSource incoming = new(); using CancellationTokenSource host = new();
            using UiFixture ui = new(h, ProgressActivity(), clock, host.Token);
            await ui.Progress.StartAsync(default);
            ui.Wire.Respond = (_, _) =>
            {
                incoming.Cancel();
                return Task.FromResult(UiTransport.Fail(HttpStatusCode.TooManyRequests, new(TimeSpan.FromHours(1))));
            };
            clock.Advance(3000);
            bool cancelled = false;
            try { await ui.Progress.ReportAsync(DirectStage.Model, 1, incoming.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Must(cancelled && ui.Wire.Requests.Count == 2);
            Task final = ui.Progress.CompleteAsync("cancelled");
            Must(!final.IsCompleted && ui.Wire.Requests.Count == 2);
            host.Cancel(); await final;
            Must(!ui.Progress.Delivered && ui.Wire.Requests.Count == 2);
        });
        await check("Direct transient final delivery recovers same update without rerunning writes", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            MessageActivity a = ProgressActivity("Create two");
            using UiFixture ui = new(h, a);
            ui.Wire.Respond = (_, _) => Task.FromResult(ui.Wire.Requests.Count == 2 ?
                UiTransport.Fail(HttpStatusCode.ServiceUnavailable, new(TimeSpan.Zero)) : UiTransport.Ok());
            ModelHandler model = new(TwoWrites(), Completion("Done"));
            string? fallback = await ui.Progress.RunAsync(() => App(h, model, DirectOptions()).Handle(a, default, ui.Progress));
            Must(fallback is null && ui.Progress.Delivered && ui.Wire.Requests.Count == 3 && model.Requests.Count == 2 &&
                h.Handler.Mutations == 2 && ui.Wire.Requests[1].Body.GetRawText() == ui.Wire.Requests[2].Body.GetRawText());
        });
        foreach (bool date in new[] { false, true })
            await check("Direct same-message update respects Retry-After " + (date ? "date" : "delta"), async () =>
            {
                await using Harness h = new(); using UiFixture ui = new(h, ProgressActivity());
                await ui.Progress.StartAsync(default);
                DateTimeOffset? until = null;
                ui.Wire.Respond = (_, _) =>
                {
                    if (ui.Wire.Requests.Count == 2)
                    {
                        RetryConditionHeaderValue retry;
                        if (date)
                        {
                            until = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
                            retry = new(until.Value);
                        }
                        else { until = DateTimeOffset.UtcNow.AddMilliseconds(40); retry = new(TimeSpan.FromMilliseconds(40)); }
                        return Task.FromResult(UiTransport.Fail(HttpStatusCode.TooManyRequests, retry));
                    }
                    Must(DateTimeOffset.UtcNow >= until);
                    return Task.FromResult(UiTransport.Ok());
                };
                await ui.Progress.CompleteAsync("same final");
                Must(ui.Progress.Delivered && ui.Wire.Requests.Count == 3 &&
                    ui.Wire.Requests[1].Body.GetRawText() == ui.Wire.Requests[2].Body.GetRawText());
            });
        await check("Direct host shutdown cancels terminal delivery without a duplicate or unbounded wait", async () =>
        {
            await using Harness h = new(); using CancellationTokenSource host = new();
            using UiFixture ui = new(h, ProgressActivity(), host: host.Token);
            await ui.Progress.StartAsync(default); host.Cancel();
            await ui.Progress.CompleteAsync("must not send");
            Must(!ui.Progress.Delivered && ui.Wire.Requests.Count == 1);
        });
        foreach (string field in new[] { "agenticAppId", "agenticUserId", "serviceUrl" })
            await check("Direct incomplete AU delivery reference fails closed before initial send " + field, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                MessageActivity a = ProgressActivity();
                if (field == "agenticAppId") a.Recipient!.AgenticAppId = null;
                else if (field == "agenticUserId") a.Recipient!.AgenticUserId = null;
                else a.ServiceUrl = new("https://teams.example.invalid/?secret=PRIVATE");
                using UiFixture ui = new(h, a);
                ModelHandler model = new(Completion("never"));
                string? result = await ui.Progress.RunAsync(() => App(h, model, DirectOptions()).Handle(a, default, ui.Progress));
                Must(ui.Progress.Attempted && result is null && ui.Wire.Requests.Count == 0 && model.Requests.Count == 0 &&
                    !string.Join("", ui.Log.Lines).Contains("PRIVATE"));
            });
        foreach (string field in new[] { "sender", "service", "thread", "policy" })
            await check("Direct terminal reference or authorization change withholds update " + field, async () =>
            {
                await using Harness h = new(); MessageActivity a = ProgressActivity();
                using UiFixture ui = new(h, a); await ui.Progress.StartAsync(default);
                if (field == "sender") a.Recipient!.AgenticUserId = Harness.Other;
                else if (field == "service") a.ServiceUrl = new("https://different.example.invalid/");
                else if (field == "thread") a.Conversation!.Id = Harness.Channel + ";messageid=different";
                else h.Policy.Contexts = [];
                await ui.Progress.CompleteAsync("sensitive answer withheld");
                Must(!ui.Progress.Delivered && ui.Wire.Requests.Count == 1 && ui.Log.Lines.Count > 0);
            });
        await check("Direct signed-in human WorkIQ profile still sends Teams updates as AU", async () =>
        {
            await using Harness h = new(); DirectEnable(h); await h.Connect(Harness.Human);
            MessageActivity a = ProgressActivity("Read my data", "personal");
            using UiFixture ui = new(h, a);
            ModelHandler model = new(Completion("No read needed."));
            await ui.Progress.RunAsync(() => App(h, model, DirectOptions("SignedInHuman")).Handle(a, default, ui.Progress));
            Must(ui.Progress.Delivered && ui.Wire.Requests.All(r => r.Identity?.AgenticUserId == Harness.Au));
        });
        foreach (string mode in new[] { "slash", "guarded", "disabled", "obsolete", "upload-only", "signin-needed", "unauthorized" })
            await check("Direct Working UI leaves unrelated and ineligible modes unchanged " + mode, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                MessageActivity a = ProgressActivity(mode switch
                { "slash" => "/help", "obsolete" => "/mcp-confirm old-id", "upload-only" => "", _ => "Hello" },
                    mode == "signin-needed" ? "personal" : "channel");
                if (mode == "unauthorized") a.From!.AadObjectId = Guid.NewGuid().ToString();
                using UiFixture ui = new(h, a);
                NaturalLanguageOptions options = mode switch
                {
                    "disabled" => Options(false), "guarded" => Options(),
                    "signin-needed" => DirectOptions("SignedInHuman"), _ => DirectOptions()
                };
                ModelHandler model = new(Completion("Hello there."));
                string? fallback = await ui.Progress.RunAsync(() => App(h, model, options).Handle(a, default, ui.Progress));
                Must(!ui.Progress.Attempted && ui.Wire.Requests.Count == 0 &&
                    model.Requests.Count == 0, fallback ?? "");
                if (mode != "disabled") Must(fallback is not null);
            });
        await check("Direct response handler does not change unmarked Teams SDK delivery errors", async () =>
        {
            await using Harness h = new(); MessageActivity a = ProgressActivity();
            using UiFixture ui = new(h, a);
            ui.Wire.Respond = (_, _) => Task.FromResult(UiTransport.Fail(HttpStatusCode.TooManyRequests, new(TimeSpan.FromSeconds(9))));
            MessageActivity outbound = new() { From = a.Recipient, Conversation = a.Conversation, ServiceUrl = a.ServiceUrl, Text = "ordinary reply" };
            try { await ui.Client.CreateAsync(a.Conversation!.Id, outbound); throw new Exception("Expected HTTP failure"); }
            catch (HttpRequestException error) { Must(error is not ProgressDeliveryException && error.StatusCode == HttpStatusCode.TooManyRequests); }
            Must(!ui.Wire.Requests.Single().Marked);
        });
        await check("Direct simultaneous invocations retain distinct outgoing IDs and auth references", async () =>
        {
            await using Harness h = new();
            MessageActivity a = ProgressActivity(), b = ProgressActivity();
            b.Conversation!.Id = Harness.Channel + ";messageid=other-thread";
            using UiFixture one = new(h, a); using UiFixture two = new(h, b);
            one.Wire.Respond = (_, _) => Task.FromResult(UiTransport.Ok("first-own"));
            two.Wire.Respond = (_, _) => Task.FromResult(UiTransport.Ok("second-own"));
            await Task.WhenAll(one.Progress.StartAsync(default), two.Progress.StartAsync(default));
            await Task.WhenAll(one.Progress.CompleteAsync("one"), two.Progress.CompleteAsync("two"));
            Must(one.Wire.Requests.Last().Url.AbsolutePath.EndsWith("/first-own") &&
                two.Wire.Requests.Last().Url.AbsolutePath.EndsWith("/second-own") &&
                one.Wire.Requests.Last().Body.GetProperty("conversation").GetProperty("id").GetString() == a.Conversation!.Id &&
                two.Wire.Requests.Last().Body.GetProperty("conversation").GetProperty("id").GetString() == b.Conversation.Id &&
                !ProgressDeliveryScope.Active);
        });
    }
}
