using System.Text.Json;
using Microsoft.Teams.Apps.Schema;
namespace WorkIqFiles;

internal static class PrivateContext
{
    internal static JsonElement Capture(MessageActivity activity)
    {
        // Only SDK addressing/authority fields survive. No text, cards, attachment bytes, or arbitrary extensions.
        JsonElement all = JsonSerializer.SerializeToElement(activity, JsonSerializerOptions.Web);
        return JsonSerializer.SerializeToElement(new
        {
            type = "message", activity.Id, activity.ReplyToId, activity.ChannelId, activity.ServiceUrl,
            from = new { activity.From?.Id, activity.From?.AadObjectId },
            recipient = new
            {
                activity.Recipient?.Id, activity.Recipient?.AadObjectId, activity.Recipient?.AgenticAppId,
                activity.Recipient?.AgenticUserId, activity.Recipient?.AgenticAppBlueprintId, activity.Recipient?.TenantId
            },
            conversation = new { activity.Conversation?.Id, activity.Conversation?.ConversationType,
                activity.Conversation?.IsGroup, activity.Conversation?.TenantId },
            channelData = new
            {
                tenant = new { id = FileCommand.Field(all, "channelData", "tenant", "id") },
                team = FileCommand.Field(all, "channelData", "team", "aadGroupId") is { } team ? new { aadGroupId = team } : null,
                channel = (FileCommand.Field(all, "channelData", "channel", "id") ??
                    FileCommand.Field(all, "channelData", "teamsChannelId")) is { } channel ? new { id = channel } : null
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }
    internal static MessageActivity Activity(JsonElement reference, string text = "")
    {
        MessageActivity activity = reference.Deserialize<MessageActivity>(JsonSerializerOptions.Web)
            ?? throw new LabException("Private job addressing is unavailable.");
        activity.Text = text;
        return activity;
    }
    internal static void RequireSender(MessageActivity activity, LabSettings settings)
    {
        settings.ValidateInvocationIdentity(activity);
        if (!LabSettings.SameGuid(activity.Recipient?.AgenticAppId, settings.AgentIdentityClientId) ||
            !LabSettings.SameGuid(activity.Recipient?.AgenticUserId, settings.AgentUserObjectId) ||
            string.IsNullOrWhiteSpace(activity.From?.Id) ||
            activity.ServiceUrl is not { IsAbsoluteUri: true, Scheme: "https" } url ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new LabException("Private analysis requires a complete authenticated Teams AU/user/service reference.");
    }
    internal static void RequirePersonalMetadata(MessageActivity activity, LabSettings settings,
        bool trustBoundCallbackGroupFlag = false)
    {
        JsonElement all = JsonSerializer.SerializeToElement(activity, JsonSerializerOptions.Web);
        all.TryGetProperty("channelData", out JsonElement data);
        PrivateMetadataShape Shape(string key)
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(key, out JsonElement value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return PrivateMetadataShape.AbsentOrNull;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()!.Length == 0 ? PrivateMetadataShape.EmptyString : PrivateMetadataShape.String,
                JsonValueKind.Object when !value.EnumerateObject().Any() => PrivateMetadataShape.EmptyObject,
                JsonValueKind.Object when value.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.Null) => PrivateMetadataShape.NullOnlyObject,
                JsonValueKind.Object => PrivateMetadataShape.Object,
                _ => PrivateMetadataShape.Other
            };
        }
        PrivateMetadataShape team = Shape("team"), channel = Shape("channel"),
            teamAlias = Shape("teamsTeamId"), channelAlias = Shape("teamsChannelId");
        bool? tenantMatches = activity.Conversation?.TenantId is { } tenant ? LabSettings.SameGuid(tenant, settings.TenantId) : null;
        if (activity.Conversation?.IsGroup == true && !trustBoundCallbackGroupFlag || tenantMatches == false ||
            new[] { team, channel, teamAlias, channelAlias }.Any(shape => shape != PrivateMetadataShape.AbsentOrNull))
            throw new PrivateAudienceMetadataException(activity.Conversation?.IsGroup, tenantMatches,
                team, channel, teamAlias, channelAlias, trustBoundCallbackGroupFlag);
    }
}

