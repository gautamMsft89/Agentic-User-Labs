# update_entity

Update only an exact authorized resource using the current descriptor's
entityUrl and jsonBody contract. Reuse grounded target IDs; inspect update
schema when needed. Do not synthesize schemas, repair annotations, strip
business fields or change versions to make an example work.

Conditional file updates at `/drives/{driveId}/items/{itemId}` include
`name` for rename and a supported parentReference for move. Verify intended
source/destination and drive compatibility; folder/name equality alone does
not establish identity. Preserve requested overwrite/conflict semantics.
An update body is not a binary-content upload.

## Same-drive driveItem move, not an action

Microsoft Graph documents a driveItem move as
`PATCH /drives/{driveId}/items/{itemId}` updating `parentReference`, not
`POST /drives/{driveId}/items/{itemId}/move`.
In WorkIQ, use only an eligible `update_entity` and the actual accepted
update schema at the already resolved source item. Conditional example:

```json
{"entityUrl":"/drives/{returnedSourceDriveId}/items/{returnedSourceItemId}","jsonBody":{"parentReference":{"id":"{returnedDestinationFolderId}"}}}
```

These placeholders must come from native resource evidence. Preserve the
basename/content by not changing `name` or uploading/replacing bytes. Include
`parentReference.driveId` only if required by the native schema, and only the
same established drive ID. A destination folder's top-level `id` is its ID;
its `parentReference.id` names its parent. Cross-drive moves are not supported
by this contract. Do not turn a move into copy+delete.

Keep the user's preconditions: establish source and existing destination within
the requested channel, same-drive membership, and requested collision-stop
evidence before selecting a write. Missing/ambiguous folders, a same-name
destination file or incomplete collision evidence must stop/clarify, not create
a folder, overwrite or auto-rename. Preflight reads do not guarantee atomic
no-overwrite under concurrent changes; do not claim a conditional guarantee
that the accepted native contract cannot provide.

Reuse adequate current path/schema evidence; do not repeat broad searches
merely because an action description mentions moving resources. If the actual
update schema cannot express parentReference, report unsupported without a
mutation. If the service rejects the update, report the observed failure and
stop; never invent a do_action `/move` fallback or replay uncertain effects.
Outlook message move is a different resource contract; driveItem copy is a
different action and does not authorize either as a substitute.

Reference: [Microsoft Graph driveItem move](https://learn.microsoft.com/en-us/graph/api/driveitem-move?view=graph-rest-1.0).
Graph documentation explains service semantics, not live WorkIQ exposure,
permission grants or permission to call Graph directly.

## Rename response sufficiency

For rename only, a successful native response identifying the intended item
and requested new name is sufficient evidence. Produce the final answer next;
do not fetch the item again or call get_schema/discovery solely to reconfirm
that success. Use returned data, not the proposed update body, as evidence.
Empty, ack-only, pending, ambiguous or error results do not verify completion:
report the actual acknowledged/uncertain/error status and stop without another
mutation. Do not add precautionary verification reads by default.

Further reads remain appropriate when the user explicitly requested
verification or a distinct remaining task needs them. This does not remove
necessary pre-rename target/schema resolution, change move/content-edit
contracts, or skip other requested actions. The existing loop still needs a
final model turn to consume the result and answer; this guidance cannot
guarantee avoiding model throttling.

Teams message updates require the exact chat/channel and message ID; use
`teams` for differing chat-message and channel-message contracts. Reactions
and presence may require `do_action`, not PATCH despite metadata schemas.

If an operation requires a header such as If-Match that this host cannot
supply, report unsupported. Never smuggle headers through arguments, infer
atomicity from a prior metadata read, or replay an uncertain mutation.
Returned errors are not success; preserve concurrency/partial-effect limits.
