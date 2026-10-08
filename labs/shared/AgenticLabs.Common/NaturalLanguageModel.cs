using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text.Json;
using System.Text;
using OpenAI;
using OpenAI.Chat;

namespace WorkIqFiles;

internal sealed class NaturalLanguageOptions
{
    public bool Enabled { get; set; }
    public bool FullWorkIqGuide { get; set; }
    public string Endpoint { get; set; } = "https://your-resource.services.ai.azure.com/openai/v1";
    public string Deployment { get; set; } = "gpt-6-luna";
    public string ApiKey { get; set; } = "";
    public string? ReasoningEffort { get; set; }
    public NaturalLanguageBudgetOptions Budgets { get; set; } = new();
    public DirectMcpOptions DirectMcp { get; set; } = new();
    internal const string WorkIqActivation = "Ordinary WorkIQ requests require NaturalLanguage:Enabled=true, " +
        "NaturalLanguage:DirectMcp:Enabled=true and NaturalLanguage:FullWorkIqGuide=true (progressive skill activation). " +
        "No guarded fallback, model or WorkIQ data call was started. Only operational auth, private-job, help and status controls remain.";
    internal void RequireNativeWorkIq()
    {
        if (!Enabled || !DirectMcp.Enabled || !FullWorkIqGuide) throw new LabException(WorkIqActivation);
    }
    internal NaturalLanguageBudgets ResolveBudgets() => Budgets.Resolve(DirectMcp.Enabled);
    internal Uri Validate()
    {
        _ = ResolveBudgets();
        if (DirectMcp.Enabled) DirectMcp.Validate();
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new LabException("Natural-language mode needs NaturalLanguage:ApiKey in the app's secure environment. Operational controls remain available.");
        if (Deployment.Length is < 1 or > 128 || Deployment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new LabException("Invalid Azure model deployment name.");
        if (ReasoningEffort is not (null or "" or "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
            throw new LabException("Invalid configured reasoning effort; use an explicitly supported deployment value or leave it unset.");
        if (!Uri.TryCreate(Endpoint.TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/openai/v1/" ||
            !(uri.Host.EndsWith(".services.ai.azure.com", StringComparison.Ordinal) ||
              uri.Host.EndsWith(".openai.azure.com", StringComparison.Ordinal)) ||
            uri.AbsoluteUri != Endpoint.TrimEnd('/') + "/")
            throw new LabException("Azure endpoint must be a canonical HTTPS Azure host ending /openai/v1, with no port, query, credentials or redirects.");
        return uri;
    }
}

// Only the model key enters this client. It never receives a WorkIQ bearer, browser cookie or MCP transport.
internal sealed class NaturalLanguageModel : IDisposable
{
    private readonly HttpClient http;
    internal ChatClient Client { get; }
    internal NaturalLanguageModel(NaturalLanguageOptions options, HttpMessageHandler? transport = null)
    {
        Uri endpoint = options.Validate();
        NaturalLanguageBudgets budgets = options.ResolveBudgets();
        http = new(new ModelEgress(endpoint, budgets)
        {
            InnerHandler = transport ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }
        }) { Timeout = Timeout.InfiniteTimeSpan };
        Client = new ChatClient(options.Deployment, new ApiKeyCredential(options.ApiKey), new OpenAIClientOptions
        {
            Endpoint = endpoint, Transport = new HttpClientPipelineTransport(http),
            NetworkTimeout = TimeSpan.FromSeconds(budgets.RequestSeconds),
            RetryPolicy = new ClientRetryPolicy(0), EnableDistributedTracing = false,
            ClientLoggingOptions = new()
            {
                EnableLogging = false, EnableMessageLogging = false, EnableMessageContentLogging = false
            }
        });
    }
    public void Dispose() => http.Dispose();
}

internal sealed class ModelEgress(Uri endpoint, NaturalLanguageBudgets? configured = null) : DelegatingHandler
{
    private readonly NaturalLanguageBudgets budgets = configured ?? NaturalLanguageBudgets.Standard;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != new Uri(endpoint, "chat/completions").AbsoluteUri)
            throw new LabException("Model HTTP destination/method denied before dispatch.");
        if (request.Content is not null)
        {
            if (request.Content.Headers.ContentLength is long declared)
                NaturalLanguageBudgets.Require("ModelRequestBytes", declared, budgets.ModelRequestBytes);
            await using Stream source = await request.Content.ReadAsStreamAsync(ct);
            using MemoryStream body = new();
            byte[] block = new byte[8192];
            int read;
            while ((read = await source.ReadAsync(block.AsMemory(), ct)) > 0)
            {
                NaturalLanguageBudgets.Require("ModelRequestBytes", body.Length + read, budgets.ModelRequestBytes);
                body.Write(block, 0, read);
            }
            HttpContent old = request.Content;
            request.Content = new ByteArrayContent(body.ToArray());
            foreach (var header in old.Headers) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            old.Dispose();
        }
        HttpResponseMessage response = await base.SendAsync(request, ct);
        PrivateIngressDiagnostic.ObserveHttp(response.StatusCode);
        try
        {
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new LabException("Model redirect rejected; credentials not forwarded.");
            if (!response.IsSuccessStatusCode)
                throw new LabException(await AzureModelDiagnostic.Read(response, ct));
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using MemoryStream bytes = new();
            byte[] buffer = new byte[8192];
            int size;
            while ((size = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                NaturalLanguageBudgets.Require("ModelResponseBytes", bytes.Length + size, budgets.ModelResponseBytes);
                bytes.Write(buffer, 0, size);
            }
            byte[] payload = bytes.ToArray();
            using (JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 }))
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out JsonElement choices) ||
                    choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1 ||
                    choices[0].ValueKind != JsonValueKind.Object || !choices[0].TryGetProperty("message", out JsonElement message) ||
                    message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("role", out JsonElement role) ||
                    role.ValueKind != JsonValueKind.String || role.GetString() != "assistant")
                    throw new LabException("Malformed model API choice/message; no completion accepted.");
            }
            HttpContent old = response.Content;
            response.Content = new ByteArrayContent(payload);
            foreach (var header in old.Headers) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            old.Dispose();
            return response;
        }
        catch { response.Dispose(); throw; }
    }
}

internal static class NaturalLanguageText
{
    internal static string Bounded(string value, int max, string budget)
    {
        NaturalLanguageBudgets.Require(budget, value.Length, max);
        return value;
    }
    internal static string Clean(string value, int max)
    {
        return value.Length <= max ? value : "[content omitted: length limit]";
    }
}
