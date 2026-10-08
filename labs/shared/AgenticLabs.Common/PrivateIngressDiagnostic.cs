using System.ClientModel;
using System.Net;

namespace WorkIqFiles;

internal enum PrivateIngressStage
{
    ResolveServices, ContextAuthorization, SenderValidation, QueueWait, RecoveryLookup, RecoveryAuthorization,
    RecoveryPersist, StatusSend, InputValidation, AttachmentMetadata, ReplayLookup, RouterConfiguration,
    RouterRequest, RouterChoice, JobPersist, RememberedRouteLookup, PersonalCreate, PersonalCreateResponse,
    PersonalPolicy, PersonalBinding, PersonalPersist, ApprovalAuthorization, ApprovalUpdate,
    ApprovalSendMarker, ApprovalSend, ApprovalAcknowledgment, ChannelReply,
    InvokeParse, InvokeQueue, InvokeAuthorization, InvokeHumanGate, InvokeConsent, InvokeSignInPreparation
}
internal enum PrivatePersistence { NotAttempted, Unconfirmed, Confirmed, Existing }
internal enum PrivateRouteSource { None, Remembered, Create }
internal enum PrivatePolicyReference { None, OriginalChannel, StoredPersonal, IncomingAction, PersonalCandidate }

// Values are fixed local labels/numbers, never exception messages, IDs, request text or provider bodies.
internal sealed class PrivateIngressDiagnostic : IDisposable
{
    private static readonly AsyncLocal<PrivateIngressDiagnostic?> current = new();
    private readonly PrivateIngressDiagnostic? previous = current.Value;
    internal string Correlation { get; } = Guid.NewGuid().ToString("N");
    internal PrivateIngressStage Stage { get; private set; } = PrivateIngressStage.ResolveServices;
    internal PrivatePersistence Persistence { get; set; }
    internal PrivateRouteSource RouteSource { get; set; }
    internal int? HttpStatus { get; private set; }
    internal bool BoundCallbackGroupFlagTrusted { get; private set; }
    internal static void ObserveBoundCallbackGroupFlag()
    {
        if (current.Value is { } scope) scope.BoundCallbackGroupFlagTrusted = true;
    }
    private PrivatePolicyReference policyReference;
    private bool? requesterMatchesJob, chatMatchesJob, tenantMatchesJob;
    private string modelCode = "unavailable", modelType = "unavailable", modelParameter = "unavailable", modelClassification = "unavailable";
    internal static PrivateIngressDiagnostic? Current => current.Value;
    internal PrivateIngressDiagnostic() => current.Value = this;
    internal static void Enter(PrivateIngressStage stage)
    {
        if (current.Value is not { } scope) return;
        scope.Stage = stage; scope.HttpStatus = null;
        scope.modelCode = scope.modelType = scope.modelParameter = scope.modelClassification = "unavailable";
        scope.policyReference = PrivatePolicyReference.None;
        scope.requesterMatchesJob = scope.chatMatchesJob = scope.tenantMatchesJob = null;
    }
    internal static void ObserveHttp(HttpStatusCode status)
    {
        if (current.Value is { } scope) scope.HttpStatus = (int)status;
    }
    internal static void ObservePolicyReference(PrivatePolicyReference reference, bool requesterMatches,
        bool? chatMatches, bool tenantMatches)
    {
        if (current.Value is not { } scope) return;
        scope.policyReference = reference; scope.requesterMatchesJob = requesterMatches;
        scope.chatMatchesJob = chatMatches; scope.tenantMatchesJob = tenantMatches;
    }
    // Called only with AzureModelDiagnostic's existing allowlisted projection, never a provider message.
    internal static void ObserveModelError(string code, string type, string parameter, string classification)
    {
        if (current.Value is not { } scope) return;
        scope.modelCode = code; scope.modelType = type; scope.modelParameter = parameter; scope.modelClassification = classification;
    }
    internal static string Kind(Exception error) => error switch
    {
        HttpRequestException => "HTTP", OperationCanceledException => "Cancelled",
        ClientResultException => "ModelSdk", System.Text.Json.JsonException => "Json",
        IOException => "IO", UnauthorizedAccessException => "LocalAccess",
        Microsoft.Identity.Client.MsalException => "Identity",
        Microsoft.Identity.Web.MicrosoftIdentityWebChallengeUserException => "IdentityChallenge",
        LabException => "Validation", InvalidOperationException => "InvalidOperation", _ => "Unexpected"
    };
    internal static string Reason(Exception error) => error is LabException ? error.Message switch
    {
        "Denied: trusted command sender and configured tenant are required." => "MissingTenantOrHuman",
        "Denied: inbound recipient lacks a trusted AU object ID." => "MissingAuRecipient",
        "Denied: inbound recipient does not match the configured Teams AU/agent/blueprint." => "AuRecipientMismatch",
        "Denied: commands must originate from an approved human test requester, not the AU." => "AuIsRequester",
        "Denied: context/requester is not an approved synthetic test audience." => "ContextOrRequesterNotApproved",
        "Conflicting channel IDs." => "ConflictingChannelIdentifiers",
        "Denied: missing or conflicting trusted team/channel." => "ChannelBindingMismatch",
        "Denied: chat contains unexpected channel context." => "UnexpectedChannelInChat",
        "Private analysis requires a complete authenticated Teams AU/user/service reference." => "IncompleteAuUserServiceReference",
        "Private route create response has no usable conversation ID." => "MissingCreatedConversation",
        "Private route create response changed the service URL." => "ChangedServiceUrl",
        "Private delivery requires an approved personal reference." => "NotPersonal",
        "Private action must come from the matching approved personal-chat human." => "PersonalOwnerOrAudienceMismatch",
        "Private request belongs to a different personal conversation." => "DifferentPersonalConversation",
        "Private action has conflicting group, channel or tenant metadata." => "ConflictingPrivateAudienceMetadata",
        "Private card invoke does not match its established personal destination." => "BoundPrivateCardMismatch",
        "Private message delivery acknowledgment unavailable; no automatic resend." => "MissingSendAcknowledgment",
        "Private pending-job limit or duplicate request." => "QueueFullOrDuplicate",
        "Private state record is malformed or outside bounds." => "StateRecordBounds",
        "Private state persistence failed; execution disabled. Details withheld." => "PersistenceFailed",
        "Private state unavailable; no execution." => "PersistenceUnavailable",
        "Private request no longer authorized, expired, or terminal." => "JobConfigurationExpiryOrTerminal",
        "Private request's original audience/owner is no longer approved." => "OriginalAudienceChanged",
        "Private intent routing did not return a single valid choice." => "RouterChoiceCountFinishOrRefusal",
        "Private intent routing cannot supply instructions, target identity or destination." => "RouterArgumentsOrTool",
        "Private routing requires the real current activity ID." => "MissingCurrentActivity",
        "Private action requires an adaptive-card invoke." => "WrongInvokeName",
        "Private approval requires an explicit manual card action with a bound job/revision." => "InvalidManualActionShape",
        "Private action revision is unavailable." => "InvalidRevision",
        "Private action unavailable." => "DisabledOrUnknownAction",
        "Private request unavailable or expired." => "JobUnavailableOrExpired",
        "Private card is expired or was already used." => "StaleRevision",
        "Private approval expired or was consumed." => "ApprovalAlreadyConsumed",
        "Private sign-in correlation was revoked, changed or expired." => "SignInGrantMismatchOrExpired",
        "Private card delivery must be established before sign-in." => "NoAcknowledgedPrivateCard",
        "Sign-in queue full; wait for link expiry." => "SignInQueueFull",
        "Natural-language mode needs NaturalLanguage:ApiKey in the app's secure environment. Slash commands remain available." => "MissingModelCredential",
        "Invalid Azure model deployment name." => "InvalidModelDeployment",
        "Invalid configured reasoning effort; use an explicitly supported deployment value or leave it unset." => "InvalidModelReasoningOption",
        "Azure endpoint must be a canonical HTTPS Azure host ending /openai/v1, with no port, query, credentials or redirects." => "InvalidModelEndpoint",
        _ => "ValidationOther"
    } : "NotApplicable";
    internal void Log(ILogger logger, Exception error, bool routeFallback)
    {
        PrivateStateValidationException? state = error as PrivateStateValidationException;
        AudienceMismatchException? audience = error as AudienceMismatchException;
        PrivateAudienceMetadataException? metadata = error as PrivateAudienceMetadataException;
        int? status = HttpStatus;
        for (Exception? item = error; item is not null && status is null; item = item.InnerException)
            status = item switch { HttpRequestException { StatusCode: { } code } => (int)code,
                ClientResultException client when client.Status is >= 100 and <= 599 => client.Status, _ => null };
        logger.LogWarning("Private ingress diagnostic: correlation={Correlation}; boundary={Boundary}; stage={Stage}; kind={Kind}; reason={Reason}; metadataConflict={MetadataConflict}; isGroup={IsGroup}; conversationTenantMatches={ConversationTenantMatches}; teamShape={TeamShape}; channelShape={ChannelShape}; teamAliasShape={TeamAliasShape}; channelAliasShape={ChannelAliasShape}; http={HttpStatus}; persistence={Persistence}; route={Route}; modelCode={ModelCode}; modelType={ModelType}; modelParameter={ModelParameter}; modelClassification={ModelClassification}; stateInvariant={StateInvariant}; observed={Observed}; maximum={Maximum}; unit={Unit}; policyReference={PolicyReference}; conversationKnown={ConversationKnown}; typeMatched={TypeMatched}; personalConversationKnown={PersonalConversationKnown}; requesterAllowed={RequesterAllowed}; actualType={ActualType}; threadSuffix={ThreadSuffix}; requesterMatchesJob={RequesterMatchesJob}; chatMatchesJob={ChatMatchesJob}; tenantMatchesJob={TenantMatchesJob}. No raw details.",
            Correlation, routeFallback ? "RouteFallback" : "OuterFailure", Stage, Kind(error), Reason(error),
            metadata?.Conflict ?? "unavailable", Flag(metadata?.IsGroup), Flag(metadata?.ConversationTenantMatches),
            metadata?.Team.ToString() ?? "unavailable", metadata?.Channel.ToString() ?? "unavailable",
            metadata?.TeamAlias.ToString() ?? "unavailable", metadata?.ChannelAlias.ToString() ?? "unavailable",
            status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable", Persistence, RouteSource,
            modelCode, modelType, modelParameter, modelClassification, state?.Invariant.ToString() ?? "unavailable",
            state?.Observed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable",
            state?.Maximum?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable",
            state?.Unit.ToString() ?? "None", policyReference, Flag(audience?.ConversationKnown), Flag(audience?.TypeMatched),
            Flag(audience?.PersonalConversationKnown), Flag(audience?.RequesterAllowed), audience?.ActualType ?? "unavailable",
            Flag(audience?.ThreadSuffix), Flag(requesterMatchesJob), Flag(chatMatchesJob), Flag(tenantMatchesJob));
    }
    private static string Flag(bool? value) => value is { } flag ? flag ? "true" : "false" : "unavailable";
    internal void LogEmptyRecovery(ILogger logger, bool matchingTerminal)
    {
        logger.LogWarning("Private recovery lookup: correlation={Correlation}; stage=RecoveryLookup; outcome={Outcome}. No raw details.",
            Correlation, matchingTerminal ? "OnlyMatchingTerminalRecords" : "NoMatchingUnexpiredRecord");
    }
    public void Dispose() => current.Value = previous;
}
