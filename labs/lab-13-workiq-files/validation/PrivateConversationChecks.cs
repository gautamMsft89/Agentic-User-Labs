using System.Net;
using System.Text.Json;
using Microsoft.Teams.Core;
using Microsoft.Teams.Core.Schema;
using Microsoft.Teams.Apps.Schema;

internal static partial class NaturalLanguageChecks
{
    // Protocol evidence only: these fixtures neither implement installation nor claim proactive AU delivery works live.
    private static async Task PrivateConversationChecks(Func<string, Func<Task>, Task> check)
    {
        await check("Private handoff SDK personal create uses exact tenant requester and explicit AU identity", async () =>
        {
            await using Harness h = new();
            var incoming = ProgressActivity();
            using UiFixture ui = new(h, incoming);
            ui.Wire.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                { id = "19:personal", serviceUrl = incoming.ServiceUrl!.AbsoluteUri }))
            });
            ConversationParameters parameters = new()
            {
                IsGroup = false, TenantId = Harness.Tenant, Bot = incoming.Recipient,
                Members = [new ChannelAccount { Id = incoming.From!.Id }]
            };
            CreateConversationResponse response = await ui.Conversations.CreateAsync(parameters,
                AgenticIdentity.FromAccount(incoming.Recipient), cancellationToken: default);
            UiRequest request = ui.Wire.Requests.Single();
            Must(response.Id == "19:personal" && request.Method == HttpMethod.Post &&
                request.Url.AbsolutePath == "/v3/conversations" && request.Url.Host == incoming.ServiceUrl!.Host);
            Must(request.Identity?.AgenticAppId == Harness.Agent && request.Identity.AgenticUserId == Harness.Au &&
                request.Body.GetProperty("isGroup").ValueKind == JsonValueKind.False &&
                request.Body.GetProperty("tenantId").GetString() == Harness.Tenant &&
                request.Body.GetProperty("members").GetArrayLength() == 1 &&
                request.Body.GetProperty("members")[0].GetProperty("id").GetString() == incoming.From!.Id &&
                !request.Body.TryGetProperty("activity", out _), request.Body.GetRawText());
        });
        await check("Private handoff SDK Bot parameter alone does not establish AU request authentication", async () =>
        {
            await using Harness h = new();
            var incoming = ProgressActivity();
            using UiFixture ui = new(h, incoming);
            await ui.Conversations.CreateAsync(new ConversationParameters
            {
                IsGroup = false, TenantId = Harness.Tenant, Bot = incoming.Recipient,
                Members = [new ChannelAccount { Id = incoming.From!.Id }]
            });
            // Deliberately incomplete fixture: production must never use this unauthenticated-identity shape.
            Must(ui.Wire.Requests.Single().Identity is null);
        });
        await check("Private handoff SDK personal creation denial remains a failure not an invented conversation", async () =>
        {
            await using Harness h = new();
            var incoming = ProgressActivity();
            using UiFixture ui = new(h, incoming);
            ui.Wire.Respond = (_, _) => Task.FromResult(UiTransport.Fail(HttpStatusCode.Forbidden));
            bool denied = false;
            try
            {
                await ui.Conversations.CreateAsync(new ConversationParameters
                {
                    IsGroup = false, TenantId = Harness.Tenant, Bot = incoming.Recipient,
                    Members = [new ChannelAccount { Id = incoming.From!.Id }]
                }, AgenticIdentity.FromAccount(incoming.Recipient));
            }
            catch (HttpRequestException error) { denied = error.StatusCode == HttpStatusCode.Forbidden; }
            Must(denied && ui.Wire.Requests.Count == 1);
        });
    }
}
