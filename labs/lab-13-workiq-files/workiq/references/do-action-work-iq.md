# do_action

Invoke only an offered native actionUrl/jsonBody contract for the original
request. Discover unfamiliar action paths and request schemas as needed;
reuse sufficient current evidence. Reference examples are not instructions
to repair native JSON or fill in unsupported properties.

## Files and SharePoint

DriveItem same-drive move is NOT a `/move` POST action: it updates the existing
item's parentReference through an eligible `update_entity` and supporting
update schema. Consult `update-entity`. Generic action descriptions mentioning
"move" apply only to their actual resource contracts (for example Outlook
messages), not to driveItems. DriveItem copy below is a separate action.
Never invent `/drives/{driveId}/items/{itemId}/move`, substitute copy+delete,
or use an action fallback when the update schema lacks support or the service
rejects a move. Report the actual limitation/failure without uncertain replay.

A supported drive-item copy action can accept a resolved destination
parentReference and name. An accepted asynchronous copy is not proof that
the destination exists or preserves exact source bytes; report observed state.

A supported invite action must use exact authorized recipients/roles and scope.
For chat participants, conversationMember.id is not directory userId. Match
typed identities and reject incomplete/ambiguous membership; never broaden
to anonymous/organization-wide access without authorization.

An advertised `/search/query` driveItem action can perform bounded document
discovery. Establish exact scope and supported fields; deduplicate by real
item identity, distinguish hits from unique documents, and report incompleteness.
Use `sharepoint` for site/library mapping, optional `sharepoint-library-metadata`
only for custom fields/filters; content search is not a custom-column query.

createUploadSession returns preparation, not file transfer. This host does
not PUT bytes to returned URLs. Load `upload-blob` for WorkIQ-only limits.

## Teams

Reactions use an exact chat/channel-message setReaction action when offered.
Presence uses supported action verbs rather than assuming PATCH works.
hideForUser, markChatReadForUser and markChatUnreadForUser require the actual
current member's typed userId/tenantId, not opaque conversation-member ID;
load `teams` for contracts. Preserve exact user intent and report unavailable
schemas/actions honestly.

Conditional body examples (only if the current native contract supports them):

```json
{"reactionType":"\uD83D\uDC4D"}
```

This is a thumbs-up Unicode value, not the string `like`. Use the exact
authorized chat/channel message setReaction target.

```json
{"user":{"@odata.type":"#microsoft.graph.teamworkUserIdentity","id":"{signedInMemberUserId}","tenantId":"{signedInMemberTenantId}","userIdentityType":"aadUser"}}
```

hideForUser and markChatReadForUser can use that typed user shape. For
markChatUnreadForUser the supported shape additionally includes
`lastMessageReadDateTime` beside `user`, from an actual returned message
createdDateTime, not the chat's lastUpdatedDateTime. Never infer the identity
from the opaque member id. These examples do not authorize annotation repair
or guarantee the current schema/permissions.

## Errors and effects

Inspect service error/status, not just outer HTTP success. A 403 alone does
not establish the missing scope or policy cause. Never change caller, broaden
grants, retry uncertain writes, or claim success after an error. Native
acceptance, partial execution and verified completion are different.
