using System.Text.Json;
using Microsoft.Teams.Apps.Schema;
using static WorkIqFiles.FileCommand;

namespace WorkIqFiles;

internal sealed class AudienceMismatchException(bool conversationKnown, bool typeMatched, bool personalConversationKnown,
    bool? requesterAllowed, string actualType, bool threadSuffix)
    : LabException("Denied: context/requester is not an approved synthetic test audience.")
{
    internal bool ConversationKnown { get; } = conversationKnown;
    internal bool TypeMatched { get; } = typeMatched;
    internal bool PersonalConversationKnown { get; } = personalConversationKnown;
    internal bool? RequesterAllowed { get; } = requesterAllowed;
    internal string ActualType { get; } = actualType switch
    {
        "personal" => "Personal", "channel" => "Channel", "groupChat" => "GroupChat",
        _ when string.Equals(actualType, "personal", StringComparison.OrdinalIgnoreCase) => "PersonalNonCanonicalCase",
        _ => "Other"
    };
    internal bool ThreadSuffix { get; } = threadSuffix;
}

internal sealed class FilePolicy
{
    public bool EnableMutations { get; set; }
    public int MaxItems { get; set; } = 10;
    public int MaxDepth { get; set; } = 16;
    public int MaxBatch { get; set; } = 5;
    public int MaxUploadBytes { get; set; } = 1024;
    public int ConfirmationSeconds { get; set; } = 120;
    public string UploadFolderName { get; set; } = "WorkIQ-Lab13";
    public ContextPolicy[] Contexts { get; set; } = [];
    public ApprovedLink[] Links { get; set; } = [];
    public UploadLocation[] UploadLocations { get; set; } = [];

    internal void RequireMutation(Invocation invocation, string? operation = null)
    {
        if (invocation.Policy.GroupShareChannelId.Length != 0)
            throw new LabException("Dedicated group sharing uses only its restricted direct-share workflow.");
        if (invocation.Policy.SharingRosterDiscoveryOnly)
            throw new LabException("Roster discovery only; all writes and confirmations are disabled.");
        if (!EnableMutations) throw new LabException("Mutations disabled by local FilePolicy. No preview or write; no policy changes performed.");
        if (!invocation.Policy.MutationsAllowed)
            throw new LabException("Mutations disabled for this context; no preview or write.");
        if (invocation.Policy.AutoResolveChannelFolder && !invocation.Policy.AllowsDynamicOperation(operation))
            throw new LabException("Automatic channel writes are limited to explicitly approved folder create, rename or move, artifact create or content edit, each with a separate opt-in; other mutations disabled.");
    }

    internal void Validate(bool allowInactiveGroupContexts = false)
    {
        if (MaxItems is < 1 or > 25 || MaxDepth is < 1 or > 32 || MaxBatch is < 1 or > 5 ||
            MaxUploadBytes is < 64 or > 4096 || ConfirmationSeconds is < 15 or > 300)
            throw new InvalidOperationException("FilePolicy limits outside conservative lab bounds.");
        Name(UploadFolderName);
        if (Contexts.Length == 0 || Contexts.Select(c => c.ConversationId).Distinct().Count() != Contexts.Length)
            throw new InvalidOperationException("Configure unique explicit test contexts.");
        foreach (ContextPolicy context in Contexts)
        {
            Id(context.ConversationId);
            if (context.SectionTwoEnabled &&
                (context.Type is not ("channel" or "personal") || context.SharingRosterDiscoveryOnly ||
                 context.GroupShareChannelId.Length != 0))
                throw new InvalidOperationException("Section2 permits only explicit channel or personal contexts, never sharing groups.");
            if (context.Type is not ("personal" or "groupChat" or "channel") ||
                context.RequesterIds.Length == 0 || (context.Roots.Length == 0 && !context.AutoResolveChannelFolder &&
                    !context.SharingRosterDiscoveryOnly && context.GroupShareChannelId.Length == 0 &&
                    !(allowInactiveGroupContexts && context.Type == "groupChat")))
                throw new InvalidOperationException("Each context needs type, requesters and roots.");
            if (context.GroupShareChannelId.Length != 0 || context.GroupShareEnabled || context.AutoExecuteGroupShareTestMode)
            {
                Id(context.GroupShareChannelId); Name(context.GroupShareFileName);
                if (context.Type != "groupChat" || context.RequesterIds.Length != 1 || context.Roots.Length != 0 ||
                    context.ShareRecipientIds.Length != 0 || context.SharingRosterDiscoveryOnly ||
                    context.AutoResolveChannelFolder || context.TeamId.Length != 0 || context.ChannelId.Length != 0 ||
                    context.AutoChannelFolderCreateEnabled || context.AutoChannelRenameEnabled || context.AutoChannelMoveEnabled || context.AutoChannelArtifactCreateEnabled || context.AutoChannelContentEditEnabled ||
                    context.AutoExecuteFolderCreateTestMode || context.AutoExecuteRenameTestMode || context.AutoExecuteMoveTestMode ||
                    context.UploadMappingDiagnosticEnabled ||
                    (context.GroupShareEnabled && context.EnableMutations != true) ||
                    (context.AutoExecuteGroupShareTestMode && !context.GroupShareEnabled))
                    throw new InvalidOperationException("Dedicated group sharing requires isolated group/file/channel settings and separate write/direct flags.");
                ContextPolicy? source = Contexts.SingleOrDefault(c => c.ConversationId == context.GroupShareChannelId);
                if (source is null || source.Type != "channel" || !source.AutoResolveChannelFolder ||
                    !source.RequesterIds.Contains(context.RequesterIds[0], StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Group sharing requires an existing approved automatic channel for the same requester.");
            }
            if (context.SharingRosterDiscoveryOnly &&
                (context.Type != "groupChat" || context.RequesterIds.Length != 1 || context.EnableMutations != false ||
                 context.Roots.Length != 0 || context.ShareRecipientIds.Length != 0 ||
                 context.AutoResolveChannelFolder || context.UploadMappingDiagnosticEnabled ||
                 context.AutoChannelFolderCreateEnabled || context.AutoChannelRenameEnabled || context.AutoChannelMoveEnabled || context.AutoChannelArtifactCreateEnabled || context.AutoChannelContentEditEnabled ||
                 context.AutoExecuteFolderCreateTestMode || context.AutoExecuteRenameTestMode || context.AutoExecuteMoveTestMode ||
                 context.TeamId.Length != 0 || context.ChannelId.Length != 0))
                throw new InvalidOperationException("Roster discovery needs one group requester, explicit writes=false, no roots/recipients or other modes.");
            if (context.AutoResolveChannelFolder &&
                (context.Type != "channel" || context.Roots.Length != 0))
                throw new InvalidOperationException("Automatic channel resolution requires a channel and empty Roots; static/requester roots cannot be mixed.");
            if ((context.AutoChannelFolderCreateEnabled || context.AutoChannelRenameEnabled || context.AutoChannelMoveEnabled || context.AutoChannelArtifactCreateEnabled || context.AutoChannelContentEditEnabled) &&
                (!context.AutoResolveChannelFolder || context.EnableMutations != true))
                throw new InvalidOperationException("Automatic write opt-ins require automatic channel binding and explicit context EnableMutations=true.");
            if (context.ExecutionTimeoutSeconds is < 15 or > 600)
                throw new InvalidOperationException("Context execution timeout must be 15-600 seconds.");
            if (context.UploadMappingDiagnosticEnabled &&
                (!context.AutoResolveChannelFolder || context.Type != "channel" || context.RequesterIds.Length != 1))
                throw new InvalidOperationException("Upload mapping diagnostic requires an automatic channel with exactly one approved requester.");
            if (context.AutoExecuteFolderCreateTestMode &&
                (!context.AutoResolveChannelFolder || !context.AutoChannelFolderCreateEnabled ||
                 context.EnableMutations != true || context.RequesterIds.Length != 1))
                throw new InvalidOperationException("Auto-execute test mode requires an explicitly writable automatic channel with exactly one approved requester.");
            if (context.AutoExecuteRenameTestMode &&
                (!context.AutoResolveChannelFolder || !context.AutoChannelRenameEnabled ||
                 context.EnableMutations != true || context.RequesterIds.Length != 1))
                throw new InvalidOperationException("Rename auto-execute test mode requires an explicitly rename-enabled automatic channel with exactly one approved requester.");
            if (context.AutoExecuteMoveTestMode &&
                (!context.AutoResolveChannelFolder || !context.AutoChannelMoveEnabled ||
                 context.EnableMutations != true || context.RequesterIds.Length != 1))
                throw new InvalidOperationException("Move auto-execute test mode requires an explicitly move-enabled automatic channel with exactly one approved requester.");
            foreach (string id in context.RequesterIds.Concat(context.ShareRecipientIds))
                if (!Guid.TryParse(id, out Guid guid) || guid == Guid.Empty)
                    throw new InvalidOperationException("Requester/recipient IDs must be GUIDs.");
            if (context.Type == "channel") { Id(context.ChannelId); RequireGuid(context.TeamId); }
            foreach (RootPolicy root in context.Roots)
            {
                Id(root.DriveId); Id(root.ItemId);
                if (root.Kind == "channel") { RequireGuid(root.TeamId); Id(root.ChannelId); }
                else if (root.Kind == "requester") RequireGuid(root.UserId);
                else throw new InvalidOperationException("Root Kind must be channel or requester.");
            }
            if (context.Roots.Select(r => (r.DriveId, r.ItemId)).Distinct().Count() != context.Roots.Length)
                throw new InvalidOperationException("Duplicate context roots are not permitted.");
        }
        if (Links.Select(l => (l.ConversationId, l.Url)).Distinct().Count() != Links.Length ||
            UploadLocations.Select(l => (l.DriveId, l.FolderId)).Distinct().Count() != UploadLocations.Length)
            throw new InvalidOperationException("Duplicate link/upload mappings are not permitted.");
        foreach (ApprovedLink link in Links)
        {
            ValidateShareUrl(link.Url);
            Id(link.DriveId); Id(link.ItemId);
            if (!Contexts.Any(c => c.ConversationId == link.ConversationId))
                throw new InvalidOperationException("Link must bind an approved conversation.");
        }
        foreach (UploadLocation location in UploadLocations)
        {
            Id(location.DriveId); Id(location.FolderId);
            if (!Uri.TryCreate(location.SiteUrl, UriKind.Absolute, out Uri? site) ||
                site.Scheme != "https" || !site.Host.EndsWith(".sharepoint.com", StringComparison.Ordinal) ||
                !site.IsDefaultPort || site.UserInfo.Length != 0 || site.Query.Length != 0 || site.Fragment.Length != 0 ||
                string.IsNullOrWhiteSpace(location.SiteKey) || location.SiteKey.Length > 1024 ||
                !location.ServerRelativeUrl.StartsWith('/') || location.ServerRelativeUrl.Contains('\\') ||
                location.ServerRelativeUrl.Contains('%') || location.ServerRelativeUrl.Contains('?') ||
                location.ServerRelativeUrl.Contains('#') || location.ServerRelativeUrl.Split('/').Any(s => s is "." or "..") ||
                !location.ServerRelativeUrl.StartsWith(site.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("Upload location needs approved authoritative site key and decoded site-relative folder mapping.");
        }
    }

    internal Invocation Authorize(MessageActivity activity, LabSettings settings)
    {
        settings.ValidateInvocationIdentity(activity);
        JsonElement json = JsonSerializer.SerializeToElement(activity, JsonSerializerOptions.Web);
        string conversation = Required(json, "conversation", "id");
        string type = Required(json, "conversation", "conversationType");
        string requester = Guid.Parse(activity.From!.AadObjectId!).ToString("D");
        if (LabSettings.SameGuid(requester, settings.AgentUserObjectId))
            throw new LabException("Denied: commands must originate from an approved human test requester, not the AU.");
        string? channel = Field(json, "channelData", "channel", "id");
        string? alias = Field(json, "channelData", "teamsChannelId");
        if (channel is not null && alias is not null && channel != alias) throw new LabException("Conflicting channel IDs.");
        channel ??= alias;
        string? team = Field(json, "channelData", "team", "aadGroupId");
        string baseConversation = type == "channel" ? conversation.Split(";messageid=", 2)[0] : conversation;
        ContextPolicy? policy = Contexts.SingleOrDefault(c => c.ConversationId == baseConversation && c.Type == type);
        if (policy is null || !policy.RequesterIds.Any(id => LabSettings.SameGuid(id, requester)))
            throw new AudienceMismatchException(
                Contexts.Any(c => c.ConversationId == baseConversation), policy is not null,
                Contexts.Any(c => c.ConversationId == baseConversation && c.Type == "personal"),
                policy is null ? null : false, type, conversation.Contains(";messageid=", StringComparison.Ordinal));
        if (type == "channel")
        {
            if (channel != policy.ChannelId || baseConversation != channel || !LabSettings.SameGuid(team, policy.TeamId))
                throw new LabException("Denied: missing or conflicting trusted team/channel.");
        }
        else if (channel is not null || team is not null || Field(json, "channelData", "team", "id") is not null ||
                 Field(json, "channelData", "teamsTeamId") is not null)
            throw new LabException("Denied: chat contains unexpected channel context.");
        return new(settings.TenantId, requester, conversation, type, team, channel, policy);
    }

    internal static Uri ValidateShareUrl(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.Host.EndsWith(".sharepoint.com", StringComparison.Ordinal) || value.Contains('\\'))
            throw new LabException("Sharing link must be an explicitly approved tenant SharePoint HTTPS link.");
        return uri;
    }
    private static void RequireGuid(string value)
    {
        if (!Guid.TryParse(value, out Guid id) || id == Guid.Empty) throw new InvalidOperationException("Nonempty GUID required.");
    }
}

internal sealed class ContextPolicy
{
    public bool SectionTwoEnabled { get; set; }
    public string GroupShareChannelId { get; set; } = "";
    public string GroupShareFileName { get; set; } = "";
    public bool GroupShareEnabled { get; set; }
    public bool AutoExecuteGroupShareTestMode { get; set; }
    public bool SharingRosterDiscoveryOnly { get; set; }
    public bool UploadMappingDiagnosticEnabled { get; set; }
    public bool AutoResolveChannelFolder { get; set; }
    public bool AutoChannelFolderCreateEnabled { get; set; }
    public bool AutoChannelRenameEnabled { get; set; }
    public bool AutoChannelMoveEnabled { get; set; }
    public bool AutoChannelArtifactCreateEnabled { get; set; }
    public bool AutoChannelContentEditEnabled { get; set; }
    public bool AutoExecuteFolderCreateTestMode { get; set; }
    public bool AutoExecuteRenameTestMode { get; set; }
    public bool AutoExecuteMoveTestMode { get; set; }
    public int ExecutionTimeoutSeconds { get; set; } = 120;
    public bool? EnableMutations { get; set; }
    internal bool MutationsAllowed => !SharingRosterDiscoveryOnly && GroupShareChannelId.Length == 0 && (AutoResolveChannelFolder
        ? EnableMutations == true && (AutoChannelFolderCreateEnabled || AutoChannelRenameEnabled || AutoChannelMoveEnabled || AutoChannelArtifactCreateEnabled || AutoChannelContentEditEnabled)
        : EnableMutations ?? Type != "channel");
    internal bool AllowsDynamicOperation(string? operation) => operation switch
    {
        "folder create" => AutoChannelFolderCreateEnabled,
        "file rename" => AutoChannelRenameEnabled,
        "file move" => AutoChannelMoveEnabled,
        "file generate" => AutoChannelArtifactCreateEnabled,
        "file edit" => AutoChannelContentEditEnabled,
        _ => false
    };
    public string ConversationId { get; set; } = "";
    public string Type { get; set; } = "";
    public string TeamId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string[] RequesterIds { get; set; } = [];
    public string[] ShareRecipientIds { get; set; } = [];
    public RootPolicy[] Roots { get; set; } = [];
}
internal sealed class RootPolicy
{
    public string DriveId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Kind { get; set; } = "channel";
    public string TeamId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string UserId { get; set; } = "";
    public bool AllowPersonalDataInSharedContext { get; set; }
}
internal sealed class ApprovedLink
{
    public string ConversationId { get; set; } = "";
    public string Url { get; set; } = "";
    public string DriveId { get; set; } = "";
    public string ItemId { get; set; } = "";
}
internal sealed class UploadLocation
{
    public string DriveId { get; set; } = "";
    public string FolderId { get; set; } = "";
    public string SiteUrl { get; set; } = "";
    public string SiteKey { get; set; } = "";
    public string ServerRelativeUrl { get; set; } = "";
}
internal sealed record Invocation(string Tenant, string Requester, string Conversation, string Type,
    string? Team, string? Channel, ContextPolicy Policy)
{
    internal string Binding => string.Join("|", Tenant, Requester, Conversation, Type, Team, Channel);
}
