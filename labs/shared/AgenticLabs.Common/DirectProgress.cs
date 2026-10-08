using System.Net;
using System.Text.Json;
using Microsoft.Teams.Apps.Schema;

namespace WorkIqFiles;

internal enum DirectStage { Catalog, Model, Tool, ToolReturned, GraphTool, GraphToolReturned, GraphAuthentication, GraphAuthenticationReturned, GraphChatSend, GraphChatReturned, GraphChannelSend, GraphChannelReturned }
internal interface IDirectProgress
{
    Task StartAsync(CancellationToken ct);
    Task ReportAsync(DirectStage stage, int number, CancellationToken ct);
}

// Opt-in to safe delivery errors only for this awaited SDK call, not other Teams operations.
internal sealed class ProgressDeliveryScope : IDisposable
{
    private static readonly AsyncLocal<ProgressDeliveryScope?> current = new();
    private readonly ProgressDeliveryScope? previous = current.Value;
    internal static bool Active => current.Value is not null;
    internal HttpStatusCode? Status { get; private set; }
    internal TimeSpan? RetryAfter { get; private set; }
    internal ProgressDeliveryScope() => current.Value = this;
    internal static void Observe(HttpStatusCode status, TimeSpan? retryAfter)
    {
        if (current.Value is not { } scope) return;
        scope.Status = status;
        scope.RetryAfter = retryAfter;
    }
    public void Dispose() => current.Value = previous;
}

internal sealed class ProgressDeliveryException(HttpStatusCode status, TimeSpan? retryAfter)
    : HttpRequestException("Teams progress delivery failed; body withheld.", null, status)
{
    internal TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class ProgressDeliveryHandler(TimeProvider clock) : DelegatingHandler
{
    // Internal constant in the pinned Teams.Core 1.0.8 ConversationClient; covered by SDK compatibility regression.
    internal const string ClientName = "BotConversationClient";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response = await base.SendAsync(request, ct);
        PrivateIngressDiagnostic.ObserveHttp(response.StatusCode);
        if (!ProgressDeliveryScope.Active || response.IsSuccessStatusCode) return response;
        TimeSpan? delay = response.Headers.RetryAfter?.Delta;
        if (delay is null && response.Headers.RetryAfter?.Date is { } date) delay = date - clock.GetUtcNow();
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        HttpStatusCode status = response.StatusCode;
        ProgressDeliveryScope.Observe(status, delay);
        response.Dispose();
        throw new ProgressDeliveryException(status, delay);
    }
}

// Awaited request-local UI only. No retained Context, timer loop, background work or operation retries.
internal sealed class DirectProgress(
    MessageActivity incoming, LabSettings settings, FilePolicy policy,
    Func<MessageActivity, CancellationToken, Task<string?>> send,
    Func<string, MessageActivity, CancellationToken, Task> update,
    CancellationToken hostStopping, ILogger? logger = null, TimeProvider? timeProvider = null,
    string? existingActivityId = null, string providerName = "WorkIQ") : IDirectProgress, IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim serial = new(1, 1);
    private string? id, binding, policySnapshot, referenceSnapshot;
    private MessageActivity? reference;
    private long lastUpdate;
    private bool terminal, quiet;
    private DateTimeOffset? retryNotBefore;
    internal bool Attempted { get; private set; }
    internal bool Delivered { get; private set; }
    internal string? ActivityId => id;
    internal string DeliveryState { get; private set; } = "not-started";

    internal async Task<string?> RunAsync(Func<Task<string?>> action)
    {
        string? response = null;
        try
        {
            response = await action();
            return Attempted ? null : response;
        }
        finally
        {
            if (Attempted)
                await CompleteAsync(response ?? "Direct processing stopped without a final answer. Prior effects are not undone; no operation is automatically replayed.");
        }
    }
    private static string ReferenceKey(MessageActivity a) => JsonSerializer.Serialize(new
    {
        a.ServiceUrl, a.ChannelId, Conversation = a.Conversation?.Id, a.ReplyToId,
        Sender = a.From?.AadObjectId, Recipient = a.Recipient
    }, JsonSerializerOptions.Web);

    private void Validate()
    {
        Invocation invocation = policy.Authorize(incoming, settings);
        if (binding is not null && (binding != invocation.Binding || policySnapshot != JsonSerializer.Serialize(policy) ||
            referenceSnapshot != ReferenceKey(incoming)))
            throw new LabException("Progress delivery context changed; update withheld.");
        // SDK UpdateAsync derives auth from replacement.From; missing agent fields otherwise permit app-only fallback.
        if (!LabSettings.SameGuid(incoming.Recipient?.AgenticUserId, settings.AgentUserObjectId) ||
            !LabSettings.SameGuid(incoming.Recipient?.AgenticAppId, settings.AgentIdentityClientId))
            throw new LabException("Progress requires the authenticated AU sender's explicit agent/user identity; no app-only fallback.");
        if (incoming.ServiceUrl is not { IsAbsoluteUri: true, Scheme: "https" } service ||
            service.UserInfo.Length != 0 || service.Query.Length != 0 || service.Fragment.Length != 0)
            throw new LabException("Progress requires an authenticated HTTPS Teams service reference.");
    }

    private MessageActivity Outgoing(MessageActivity content)
    {
        MessageActivity source = reference ?? throw new InvalidOperationException("Progress reference not initialized.");
        content.From = source.Recipient;
        content.Conversation = source.Conversation;
        content.ServiceUrl = source.ServiceUrl;
        content.ChannelId = source.ChannelId;
        content.ReplyToId = source.ReplyToId;
        if (id is not null) content.Id = id;
        return content;
    }

    private static MessageActivity Card(string status) => new MessageActivity().AddAttachment(
        TeamsAttachment.CreateBuilder().WithAdaptiveCard(JsonSerializer.SerializeToElement(new
        {
            type = "AdaptiveCard", version = "1.5",
            body = new object[]
            {
                new { type = "TextBlock", text = "Working", weight = "Bolder", wrap = true },
                new { type = "TextBlock", text = status, wrap = true }
            }
        })).Build());

    public async Task StartAsync(CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            if (Attempted || terminal) throw new LabException("Progress already started; initial send will not be repeated.");
            Attempted = true;
            Validate();
            binding = policy.Authorize(incoming, settings).Binding;
            policySnapshot = JsonSerializer.Serialize(policy);
            referenceSnapshot = ReferenceKey(incoming);
            reference = JsonSerializer.Deserialize<MessageActivity>(JsonSerializer.Serialize(new
                { incoming.ServiceUrl, incoming.ChannelId, incoming.Conversation, incoming.Recipient, incoming.ReplyToId }, JsonSerializerOptions.Web),
                JsonSerializerOptions.Web)!;
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, hostStopping);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            DeliveryState = "initial-send-entered";
            if (existingActivityId is not null)
            {
                if (string.IsNullOrWhiteSpace(existingActivityId) || existingActivityId.Length > 2048)
                    throw new LabException("Previously acknowledged private activity ID is invalid.");
                id = existingActivityId;
                await UpdateAsync(Outgoing(Card("Preparing privately approved work.")), deadline.Token);
                lastUpdate = clock.GetTimestamp();
                DeliveryState = "working-delivered";
                return;
            }
            using ProgressDeliveryScope scope = new();
            string? sentId = await send(Outgoing(Card($"Preparing {providerName} discovery.")), deadline.Token);
            if (string.IsNullOrWhiteSpace(sentId) || sentId.Length > 2048)
                throw new LabException("Working message has no usable returned activity ID; no model or tools started, no duplicate send.");
            id = sentId;
            lastUpdate = clock.GetTimestamp();
            DeliveryState = "working-delivered";
        }
        catch (Exception error) when (DeliveryError(error))
        {
            DeliveryState = "initial-delivery-unconfirmed";
            Log("initial", error);
            throw new LabException("Working message delivery could not be confirmed. No model or tools started; initial send is not replayed.");
        }
        finally { serial.Release(); }
    }

    public async Task ReportAsync(DirectStage stage, int number, CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            if (terminal || quiet || id is null || clock.GetElapsedTime(lastUpdate) < TimeSpan.FromSeconds(2)) return;
            string status = stage switch
            {
                DirectStage.Catalog => $"Discovering available {providerName} tools.",
                DirectStage.Model => $"Model request {number}: choosing next steps or preparing the answer.",
                DirectStage.Tool => $"Running selected {providerName} operation {number}.",
                DirectStage.ToolReturned => $"{providerName} operation {number} returned; preparing the next step.",
                DirectStage.GraphTool => $"Starting selected Graph SDK file operation {number}.",
                DirectStage.GraphToolReturned => $"Graph SDK file operation {number} returned; preparing the next step.",
                DirectStage.GraphAuthentication => "Preparing configured AU Graph authentication; no file operation implied.",
                DirectStage.GraphAuthenticationReturned => "Configured AU Graph authentication preparation returned; no file operation implied.",
                DirectStage.GraphChatSend => $"Starting selected Graph SDK chat send {number}.",
                DirectStage.GraphChatReturned => $"Graph SDK chat send {number} returned; notification delivery not independently verified.",
                DirectStage.GraphChannelSend => $"Starting selected Graph SDK channel root post {number}.",
                DirectStage.GraphChannelReturned => $"Graph SDK channel root post {number} returned; notification delivery not independently verified.",
                _ => throw new ArgumentOutOfRangeException(nameof(stage))
            };
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, hostStopping);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await UpdateAsync(Outgoing(Card(status)), deadline.Token);
            lastUpdate = clock.GetTimestamp();
        }
        catch (Exception error) when (DeliveryError(error))
        {
            quiet = true; DeliveryState = "progress-updates-paused"; Log("progress", error);
            // Optional status failure never retries model/tools. Cancellation still stops the operation at its next check.
            ct.ThrowIfCancellationRequested();
            hostStopping.ThrowIfCancellationRequested();
        }
        finally { serial.Release(); }
    }

    internal async Task CompleteAsync(string response)
    {
        // One short terminal-delivery window independent of incoming disconnect; always bounded by host shutdown.
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        bool entered = false;
        try
        {
            await serial.WaitAsync(deadline.Token); entered = true;
            if (terminal) return;
            terminal = true;
            if (!Attempted || id is null) return;
            if (response.Contains(DirectPrivateErrorReceipt.Marker, StringComparison.Ordinal) &&
                !DirectPrivateErrorReceipt.Personal(incoming, policy.Authorize(incoming, settings)))
                response = response[..response.IndexOf(DirectPrivateErrorReceipt.Marker, StringComparison.Ordinal)] +
                    "\nPrivate returned-error details omitted: delivery is not a verified personal context.";
            await UpdateAsync(Outgoing(new MessageActivity { Text = BotText.Html(response), TextFormat = TextFormats.Xml }),
                deadline.Token);
            Delivered = true; DeliveryState = "terminal-delivered";
        }
        catch (Exception error) when (DeliveryError(error))
        { DeliveryState = "terminal-delivery-unconfirmed"; Log("terminal", error); }
        finally { if (entered) serial.Release(); }
    }

    private async Task UpdateAsync(MessageActivity replacement, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Validate();
            while (retryNotBefore is { } until && until - clock.GetUtcNow() is { Ticks: > 0 } remaining)
                await Task.Delay(remaining > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : remaining, clock, ct);
            Validate();
            using ProgressDeliveryScope scope = new();
            try
            {
                await update(id!, replacement, ct);
                retryNotBefore = null;
                return;
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.TooManyRequests or
                HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                TimeSpan delay = error is ProgressDeliveryException { RetryAfter: { } retry } ? retry :
                    TimeSpan.FromSeconds(attempt);
                DeferRetry(delay);
                if (attempt >= 3) throw;
            }
            finally
            {
                // HttpClient can replace an HTTP exception with cancellation. Retain observed backoff across terminal delivery.
                if (scope.Status is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout &&
                    scope.RetryAfter is { } delay) DeferRetry(delay);
            }
        }
    }
    private void DeferRetry(TimeSpan delay)
    {
        DateTimeOffset now = clock.GetUtcNow();
        retryNotBefore = delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
    }
    private static bool DeliveryError(Exception error) => error is HttpRequestException or IOException or JsonException or
        OperationCanceledException or LabException or InvalidOperationException or ArgumentException or
        Microsoft.Identity.Client.MsalException or Microsoft.Identity.Web.MicrosoftIdentityWebChallengeUserException;
    private void Log(string phase, Exception error) =>
        logger?.LogWarning("Direct progress {Phase} delivery not confirmed; category={Category}; HTTP={Status}. No message-create/model/tool replay; details withheld.",
            phase, error is HttpRequestException ? "HTTP" : error is OperationCanceledException ? "cancellation" : "validation-or-auth",
            error is HttpRequestException http ? (int?)http.StatusCode : null);
    public void Dispose() => serial.Dispose();
}
