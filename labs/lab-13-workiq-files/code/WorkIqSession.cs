using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WorkIqFiles;

internal sealed class WorkIqProtocolException(McpProtocolException error, int? outerHttp)
    : McpProtocolException(error.Message, error, error.ErrorCode)
{
    internal int? OuterHttpStatus { get; } = outerHttp;
}
internal sealed class WorkIqHttpException(string stage, string tool, HttpRequestException error)
    : HttpRequestException("WorkIQ transport failed; backend details omitted.", error, error.StatusCode)
{
    internal string Diagnostic => $"stage={stage}; tool={tool}; outerHttp={(int?)StatusCode}; resourceStatus=unavailable";
}
internal interface IWorkIq
{
    Task<ToolReply> CallAsync(string tool, Dictionary<string, object?> arguments, bool mutation, CancellationToken ct);
    Task<ToolCatalog> ListAsync(CancellationToken ct) =>
        throw new LabException("This backend does not support authenticated tools/list.");
    Task<ToolReply> CallDirectAsync(WorkIqTool tool, Dictionary<string, object?> arguments, CancellationToken ct) =>
        throw new LabException("This backend does not support direct MCP execution.");
}

// One fixed principal per instance (AU or one human). Only safe reads get one explicit reconnect.
internal sealed class WorkIqSession : IWorkIq, IAsyncDisposable
{
    internal const string Endpoint = "https://workiq.svc.cloud.microsoft/mcp";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HttpClient http;
    private readonly HumanHttpHandler auth;
    private readonly string processMarker = Guid.NewGuid().ToString("N")[..8];
    private McpClient? client;
    private HttpClientTransport? transport;
    private int generation;
    private bool disposed;

    internal WorkIqSession(IWorkIqTokenProvider tokens, HttpMessageHandler inner)
    {
        auth = new(tokens) { InnerHandler = inner };
        http = new(auth) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ToolReply> CallAsync(string tool, Dictionary<string, object?> arguments,
        bool mutation, CancellationToken cancellationToken)
        => await CallRecorded(tool, arguments, mutation, cancellationToken, direct: false);

    public async Task<ToolReply> CallDirectAsync(WorkIqTool descriptor, Dictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        JsonElement args = JsonSerializer.SerializeToElement(arguments);
        DirectMcpContract.Validate(descriptor, args);
        return await CallRecorded(descriptor.Name, arguments, DirectMcpContract.MayMutate(descriptor, args),
            cancellationToken, direct: true);
    }

    private async Task<ToolReply> CallRecorded(string tool, Dictionary<string, object?> arguments,
        bool mutation, CancellationToken cancellationToken, bool direct)
    {
        OperationProgress? progress = OperationProgress.Current;
        CheckCallBudget(progress);
        if (tool == "tools/list") throw new LabException("Use the bounded catalog API.");
        ReadCallTrace? recorder = tool is "get_schema" or "search_paths" or "list_agents" ? progress?.DiscoveryCalls :
            progress?.SectionOneCalls ?? progress?.ReadTrace;
        ReadCallTrace.Call? trace = recorder?.Start(tool, arguments, progress!.ReadPhase);
        try
        {
            ToolReply reply = await CallCore(tool, arguments, mutation, cancellationToken, trace, direct);
            if (trace is not null) trace.Outcome = ReadCallTrace.Call.Result(reply.Result);
            return reply;
        }
        catch (OperationCanceledException)
        {
            if (trace is not null) trace.Outcome = "cancelled";
            throw;
        }
        finally { if (trace is not null) recorder!.Finish(trace); }
    }
    public async Task<ToolCatalog> ListAsync(CancellationToken ct)
    {
        CheckCallBudget(OperationProgress.Current);
        ReadCallTrace? recorder = OperationProgress.Current?.DiscoveryCalls;
        ReadCallTrace.Call? trace = recorder?.Start("tools/list", [], "catalog");
        try
        {
            ToolReply reply = await CallCore("tools/list", [], false, ct, trace);
            JsonElement data = reply.Result.StructuredContent!.Value;
            ListToolsResult result = data.Deserialize<ListToolsResult>() ??
                throw new LabException("Invalid tools/list result.");
            if (result.Tools is null || result.NextCursor is not null || result.Tools.Count > 64)
                throw new LabException("Incomplete/oversized MCP catalog; no tools exposed.");
            if (result.Tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != result.Tools.Count)
                throw new LabException("Duplicate MCP tool names; no tools exposed.");
            if (trace is not null) trace.Outcome = "returned";
            return new(result.Tools.Select(t => new WorkIqTool(t.Name, t.InputSchema.Clone(), t.Description,
                t.Annotations?.ReadOnlyHint, t.Annotations?.DestructiveHint)).ToArray(), reply.Timing);
        }
        catch (OperationCanceledException) { if (trace is not null) trace.Outcome = "cancelled"; throw; }
        finally { if (trace is not null) recorder!.Finish(trace); }
    }
    private async Task<ToolReply> CallCore(string tool, Dictionary<string, object?> arguments,
        bool mutation, CancellationToken cancellationToken, ReadCallTrace.Call? trace, bool direct = false)
    {
        if (!direct && ((tool != "fetch" && tool != "fetch_blob" && tool != "create_entity" &&
             tool != "update_entity" && tool != "do_action" &&
             tool != "tools/list" && tool != "get_schema" && tool != "search_paths") ||
            mutation != (tool is "create_entity" or "update_entity" or "do_action")))
            throw new LabException("Denied: unsupported tool or unsafe retry classification.");
        Stopwatch total = Stopwatch.StartNew();
        OperationProgress? progress = OperationProgress.Current;
        if (progress?.NaturalLanguageCallBudget is int budget)
        {
            CheckCallBudget(progress);
            progress.NaturalLanguageCallBudget = budget - 1;
        }
        if (progress is not null)
        {
            progress.Tool = tool;
            progress.Stage = "MCP session queue";
            progress.CallStarted();
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int callSeconds = progress?.NaturalLanguageBudgets?.McpCallSeconds ?? 120;
        timeout.CancelAfter(TimeSpan.FromSeconds(callSeconds));
        void CancellationDiagnostic()
        {
            if (progress is not null && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                progress.CancellationSource = $"{callSeconds}s backend call budget (includes queue/init/token)";
        }
        try { await gate.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { CancellationDiagnostic(); throw; }
        finally { if (trace is not null) trace.Queue = total.Elapsed.TotalMilliseconds; }
        double queue = total.Elapsed.TotalMilliseconds;
        CommandMeasurement measurement = new()
        {
            Blob = progress?.NaturalLanguageCallBudget is not null || tool is "tools/list" or "get_schema" or "search_paths" ||
                tool == "fetch_blob" || tool == "fetch" &&
                arguments.TryGetValue("entityUrls", out object? paths) && paths is string[] urls &&
                urls.Any(p => (p.StartsWith("/chats/", StringComparison.Ordinal) &&
                    (p.EndsWith("/members", StringComparison.Ordinal) || p.Contains("/messages/", StringComparison.Ordinal))) ||
                    (p.StartsWith("/teams/", StringComparison.Ordinal) && !p.Contains("/filesFolder", StringComparison.Ordinal))) ||
                tool == "do_action" &&
                arguments.TryGetValue("actionUrl", out object? action) && action is "/sharepoint/get_site_key",
            Progress = progress
        };
        measurement.EnvelopeLimitBytes = tool == "fetch_blob" ? BoundedBlobResponse.MaxEnvelopeBytes :
            progress?.NaturalLanguageBudgets?.McpResponseBytes ?? BoundedBlobResponse.MaxEnvelopeBytes;
        measurement.Evidence.Reset(measurement.EnvelopeLimitBytes);
        auth.Current = measurement;
        double initialization = 0, fetch = 0;
        bool toolEntered = false;
        string mode = client is null ? "newly initialized" : "reused";
        if (trace is not null) trace.Mode = client is null ? "new" : "reused";
        string stage = "MCP initialization (before requested tool)";
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            for (int attempt = 0; ; attempt++)
            {
                if (client is null)
                {
                    if (progress is not null) progress.Stage = "MCP initialization before requested tool";
                    Stopwatch init = Stopwatch.StartNew();
                    transport = new(new HttpClientTransportOptions
                    {
                        Endpoint = new Uri(Endpoint),
                        TransportMode = HttpTransportMode.StreamableHttp,
                        EnableStandaloneGetStream = false,
                        MaxReconnectionAttempts = 0
                    }, http);
                    try
                    {
                        McpClientOptions? clientOptions = progress?.NaturalLanguageBudgets is null ? null :
                            new() { InitializationTimeout = TimeSpan.FromSeconds(callSeconds) };
                        client = await McpClient.CreateAsync(transport, clientOptions, cancellationToken: timeout.Token);
                        generation++;
                    }
                    finally
                    {
                        initialization += init.Elapsed.TotalMilliseconds;
                        if (client is null) await CloseClientAsync();
                    }
                }

                CallToolResult result;
                stage = mutation ? "mutation tools/call (may have been dispatched)" : "read-only tools/call";
                Stopwatch call = Stopwatch.StartNew();
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    if (progress is not null)
                    {
                        progress.Stage = stage;
                        if (mutation) progress.MutationMayHaveDispatched = true;
                    }
                    measurement.LastHttpStatus = null;
                    measurement.Evidence.Reset(measurement.EnvelopeLimitBytes);
                    toolEntered = true;
                    if (tool == "tools/list")
                    {
                        ListToolsResult listed = await client.ListToolsAsync(new ListToolsRequestParams(), timeout.Token);
                        JsonElement catalog = JsonSerializer.SerializeToElement(listed);
                        if (progress?.NaturalLanguageBudgets is { } nlBudgets)
                            NaturalLanguageBudgets.Require("McpResponseBytes",
                                System.Text.Encoding.UTF8.GetByteCount(catalog.GetRawText()), nlBudgets.McpResponseBytes);
                        else if (catalog.GetRawText().Length > 262144) throw new LabException("MCP catalog exceeds local limit.");
                        result = new() { Content = [], StructuredContent = catalog };
                    }
                    else result = await client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
                    measurement.Evidence.Result(result);
                    if (result.Content is null || result.Content.Any(block => block is null))
                        throw new McpException("Invalid MCP content container; details withheld.");
                }
                catch (Exception error) when (!direct && !mutation && attempt == 0 && measurement.SessionExpired &&
                    !timeout.IsCancellationRequested && error is HttpRequestException or McpException)
                {
                    // SDK marks HTTP 404 with Mcp-Session-Id as expired. In-band resource 404 never reaches here.
                    await CloseClientAsync();
                    measurement.SessionExpired = false;
                    mode = "reconnected";
                    if (trace is not null) { trace.Mode = "reconnected"; trace.Reconnects++; }
                    continue;
                }
                catch (Exception error) when (mutation &&
                    error is HttpRequestException or McpException or OperationCanceledException or JsonException or IOException)
                {
                    // Once tools/call starts, transport errors cannot prove the write did not execute.
                    measurement.SessionExpired = false;
                    measurement.Evidence.Failure(error);
                    CancellationDiagnostic();
                    string? failureStage = progress?.Stage;
                    string diagnostic = $"stage=mutation tools/call (may have been dispatched); tool={tool}; " +
                        $"outerHttp={(error is HttpRequestException httpError ? ((int?)httpError.StatusCode)?.ToString() : null) ?? "unavailable"}; " +
                        $"lastHttpStage={measurement.HttpStage}; resourceStatus=unavailable. Timeout is not proof of denial or non-execution.";
                    try { await CloseClientAsync(); }
                    catch (Exception cleanup) when (cleanup is HttpRequestException or McpException or OperationCanceledException or LabException or
                        Microsoft.Identity.Client.MsalException or Microsoft.Identity.Web.MicrosoftIdentityWebChallengeUserException)
                    {
                        throw new UnknownOutcomeException(cleanup is HumanSignInRequiredException ||
                            error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }, diagnostic);
                    }
                    finally { if (progress is not null && failureStage is not null) progress.Stage = failureStage; }
                    throw new UnknownOutcomeException(error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }, diagnostic);
                }
                finally { fetch += call.Elapsed.TotalMilliseconds; }

                if (progress is not null) progress.CallsCompleted++;
                return new(result, new($"{processMarker}-g{generation}", mode, queue, initialization, fetch,
                    measurement.TokenMs, measurement.TokenCalls, total.Elapsed.TotalMilliseconds), measurement.LastHttpStatus);
            }
        }
        catch (HttpRequestException error)
        {
            measurement.Evidence.Failure(error);
            throw new WorkIqHttpException(stage + "; " + measurement.HttpStage, tool, error);
        }
        catch (IOException error)
        {
            measurement.Evidence.Failure(error);
            throw new WorkIqHttpException(stage + "; " + measurement.HttpStage, tool,
                new HttpRequestException("WorkIQ response stream failed; details withheld.", error));
        }
        catch (McpProtocolException error) when (tool is "get_schema" or "search_paths")
        {
            measurement.Evidence.Failure(error);
            throw new WorkIqProtocolException(error, measurement.LastHttpStatus);
        }
        catch (Exception error) when (error is McpException or JsonException)
        { measurement.Evidence.Failure(error); throw; }
        catch (OperationCanceledException error) { measurement.Evidence.Failure(error); CancellationDiagnostic(); throw; }
        finally
        {
            if (direct && progress is not null)
                progress.DirectCallEvidence = measurement.Evidence.Snapshot(
                    new(toolEntered && generation > 0 ? $"{processMarker}-g{generation}" : "not established",
                        mode, queue, initialization, fetch, measurement.TokenMs, measurement.TokenCalls,
                        total.Elapsed.TotalMilliseconds), toolEntered, measurement.HttpCalls);
            try
            {
                // A second expiration fails this command; the next command may initialize afresh.
                if (measurement.SessionExpired) await CloseClientAsync();
            }
            finally
            {
                if (trace is not null)
                {
                    if (generation > 0) trace.Session = $"{processMarker}-g{generation}";
                    trace.Init = initialization; trace.ToolMs = fetch;
                    trace.Auth = measurement.TokenMs; trace.Tokens = measurement.TokenCalls;
                    trace.Http = measurement.HttpCalls;
                }
                auth.Current = null;
                gate.Release();
            }
        }
    }

    private static void CheckCallBudget(OperationProgress? progress)
    {
        if (progress?.NaturalLanguageCallBudget is <= 0)
            throw new LabException("Natural-language WorkIQ call budget exhausted; " +
                $"limit [McpCalls], completed/started [{progress.CallsStarted}], remaining [0], limit [{progress.NaturalLanguageBudgets?.McpCalls.ToString() ?? "invocation override"}].");
    }

    private async ValueTask CloseClientAsync()
    {
        McpClient? oldClient = client;
        HttpClientTransport? oldTransport = transport;
        client = null;
        transport = null;
        try
        {
            if (oldClient is not null) await oldClient.DisposeAsync();
        }
        finally
        {
            if (oldTransport is not null) await oldTransport.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            try { await CloseClientAsync(); }
            finally { http.Dispose(); }
        }
        finally { gate.Release(); }
    }

    internal sealed class CommandMeasurement
    {
        private readonly object sync = new();
        private double tokenMs;
        private int tokenCalls;
        private int httpCalls;
        internal int HttpCalls => Volatile.Read(ref httpCalls);
        internal void DispatchHttp() => Interlocked.Increment(ref httpCalls);
        internal volatile bool SessionExpired;
        internal bool Blob;
        internal int EnvelopeLimitBytes = BoundedBlobResponse.MaxEnvelopeBytes;
        internal OperationProgress? Progress;
        internal string HttpStage = "no outgoing HTTP observed";
        internal int? LastHttpStatus;
        internal readonly WorkIqResponseEvidence Evidence = new();
        internal double TokenMs { get { lock (sync) return tokenMs; } }
        internal int TokenCalls { get { lock (sync) return tokenCalls; } }
        internal void AddToken(double elapsed) { lock (sync) { tokenMs += elapsed; tokenCalls++; } }
    }

    internal sealed class HumanHttpHandler(IWorkIqTokenProvider tokens) : DelegatingHandler
    {
        internal CommandMeasurement? Current;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri != Endpoint)
                throw new LabException("Denied: MCP attempted a non-WorkIQ endpoint.");
            CommandMeasurement? measurement = Current;
            Stopwatch tokenTime = Stopwatch.StartNew();
            string token;
            if (measurement is not null) measurement.HttpStage = "token acquisition/validation";
            if (measurement is not null) measurement.Evidence.Stage = "token acquisition/validation";
            if (measurement?.Progress is { } acquiring) acquiring.Stage = "token acquisition/validation";
            try { token = await tokens.GetTokenAsync(cancellationToken); }
            finally { measurement?.AddToken(tokenTime.Elapsed.TotalMilliseconds); }
            cancellationToken.ThrowIfCancellationRequested();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (measurement is not null) measurement.HttpStage = "WorkIQ HTTP transport";
            if (measurement is not null) measurement.Evidence.Stage = "HTTP dispatch entered";
            if (measurement?.Progress is { } sending) sending.Stage = "WorkIQ HTTP transport";
            measurement?.DispatchHttp();
            HttpResponseMessage response;
            try { response = await base.SendAsync(request, cancellationToken); }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
            { measurement?.Evidence.Failure(error); throw; }
            if (measurement is not null) measurement.LastHttpStatus = (int)response.StatusCode;
            if (measurement is not null)
            {
                measurement.Evidence.Http = (int)response.StatusCode;
                measurement.Evidence.DeclaredBytes = response.Content.Headers.ContentLength;
                measurement.Evidence.Stage = "response headers received";
                measurement.Evidence.Media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() switch
                {
                    "application/json" => "application/json", "text/event-stream" => "text/event-stream",
                    _ => "other-or-absent"
                };
            }
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                response.Dispose();
                throw new HttpRequestException("WorkIQ redirects are forbidden.");
            }
            if (measurement is not null && request.Method == HttpMethod.Post &&
                response.StatusCode == HttpStatusCode.NotFound &&
                request.Headers.Contains("Mcp-Session-Id"))
                measurement.SessionExpired = true;
            if (measurement?.Blob == true)
            {
                if (response.Content.Headers.ContentLength > measurement.EnvelopeLimitBytes)
                {
                    long declaredBytes = response.Content.Headers.ContentLength.Value;
                    response.Dispose();
                    WorkIqEnvelopeLimitException error = new(declaredBytes, atLeast: false, measurement.EnvelopeLimitBytes);
                    measurement.Evidence.Failure(error);
                    throw error;
                }
                response.Content = new BoundedBlobResponse(response.Content, measurement.EnvelopeLimitBytes, measurement.Evidence);
            }
            return response;
        }
    }
}
