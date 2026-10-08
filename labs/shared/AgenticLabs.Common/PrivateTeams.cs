using System.Text.Json;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Core;
using Microsoft.Teams.Core.Http;
using Microsoft.Teams.Core.Schema;
namespace WorkIqFiles;

internal interface IPrivateTeams
{
    Task<MessageActivity> CreatePersonal(MessageActivity origin, CancellationToken ct);
    Task<string> Send(MessageActivity reference, MessageActivity content, CancellationToken ct);
    Task Update(MessageActivity reference, string id, MessageActivity content, CancellationToken ct);
}

internal sealed class PrivateTeams(ConversationClient client, FilePolicy policy, LabSettings settings) : IPrivateTeams
{
    public async Task<MessageActivity> CreatePersonal(MessageActivity origin, CancellationToken ct)
    {
        PrivateContext.RequireSender(origin, settings);
        Invocation source = policy.Authorize(origin, settings);
        if (source.Type != "channel") throw new LabException("Personal conversation creation requires the approved channel source.");
        using ProgressDeliveryScope safe = new();
        PrivateIngressDiagnostic.Enter(PrivateIngressStage.PersonalCreate);
        CreateConversationResponse result = await client.CreateConversationAsync(new()
        {
            IsGroup = false, Bot = origin.Recipient, Members = [new ChannelAccount { Id = origin.From!.Id }],
            TenantId = source.Tenant, ChannelData = new { tenant = new { id = source.Tenant } }
        }, origin.ServiceUrl!, BotRequestContext.FromAgenticIdentity(AgenticIdentity.FromAccount(origin.Recipient)), cancellationToken: ct);
        PrivateIngressDiagnostic.Enter(PrivateIngressStage.PersonalCreateResponse);
        if (string.IsNullOrWhiteSpace(result.Id) || result.Id.Length > 2048)
            throw new LabException("Private route create response has no usable conversation ID.");
        if (result.ServiceUrl is not null && result.ServiceUrl != origin.ServiceUrl)
            throw new LabException("Private route create response changed the service URL.");
        // This is an SDK-created delivery reference, not an invented incoming user message or approval.
        MessageActivity personal = JsonSerializer.Deserialize<MessageActivity>(JsonSerializer.Serialize(new
        {
            type = "message", channelId = origin.ChannelId, serviceUrl = origin.ServiceUrl,
            from = origin.From, recipient = origin.Recipient,
            conversation = new { id = result.Id, conversationType = "personal", isGroup = false, tenantId = source.Tenant },
            channelData = new { tenant = new { id = source.Tenant } }
        }, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        PrivateIngressDiagnostic.Enter(PrivateIngressStage.PersonalPolicy);
        RequirePersonal(personal);
        return personal;
    }
    private void RequirePersonal(MessageActivity reference)
    {
        PrivateContext.RequireSender(reference, settings);
        PrivateContext.RequirePersonalMetadata(reference, settings);
        if (policy.Authorize(reference, settings).Type != "personal")
            throw new LabException("Private delivery requires an approved personal reference.");
    }
    private MessageActivity Outgoing(MessageActivity reference, MessageActivity content)
    {
        RequirePersonal(reference);
        content.From = reference.Recipient; content.Conversation = reference.Conversation;
        content.ServiceUrl = reference.ServiceUrl; content.ChannelId = reference.ChannelId;
        // A card action's ReplyToId does not establish a new thread for proactive personal delivery.
        return content;
    }
    public async Task<string> Send(MessageActivity reference, MessageActivity content, CancellationToken ct)
    {
        using ProgressDeliveryScope safe = new();
        string? id = (await client.SendActivityAsync(Outgoing(reference, content), cancellationToken: ct))?.Id;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 2048) throw new LabException("Private message delivery acknowledgment unavailable; no automatic resend.");
        return id;
    }
    public async Task Update(MessageActivity reference, string id, MessageActivity content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 2048) throw new LabException("Private outgoing activity ID unavailable.");
        MessageActivity message = Outgoing(reference, content); message.Id = id;
        using ProgressDeliveryScope safe = new();
        await client.UpdateActivityAsync(reference.Conversation!.Id, id, message,
            requestContext: BotRequestContext.FromActivity(message), cancellationToken: ct);
    }
}

