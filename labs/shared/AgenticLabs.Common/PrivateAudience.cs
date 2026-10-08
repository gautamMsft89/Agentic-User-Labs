namespace WorkIqFiles;

internal enum PrivateMetadataShape { AbsentOrNull, EmptyString, String, EmptyObject, NullOnlyObject, Object, Other }
internal sealed class PrivateAudienceMetadataException(bool? isGroup, bool? conversationTenantMatches,
    PrivateMetadataShape team, PrivateMetadataShape channel, PrivateMetadataShape teamAlias, PrivateMetadataShape channelAlias,
    bool trustBoundCallbackGroupFlag = false)
    : LabException("Private action has conflicting group, channel or tenant metadata.")
{
    internal bool? IsGroup { get; } = isGroup;
    internal bool? ConversationTenantMatches { get; } = conversationTenantMatches;
    internal PrivateMetadataShape Team { get; } = team;
    internal PrivateMetadataShape Channel { get; } = channel;
    internal PrivateMetadataShape TeamAlias { get; } = teamAlias;
    internal PrivateMetadataShape ChannelAlias { get; } = channelAlias;
    internal string Conflict => IsGroup == true && !trustBoundCallbackGroupFlag ? "ExplicitGroupFlag" :
        Team != PrivateMetadataShape.AbsentOrNull ? "TeamMetadata" :
        Channel != PrivateMetadataShape.AbsentOrNull ? "ChannelMetadata" :
        TeamAlias != PrivateMetadataShape.AbsentOrNull ? "TeamAliasMetadata" :
        ChannelAlias != PrivateMetadataShape.AbsentOrNull ? "ChannelAliasMetadata" : "ConversationTenantMismatch";
}
