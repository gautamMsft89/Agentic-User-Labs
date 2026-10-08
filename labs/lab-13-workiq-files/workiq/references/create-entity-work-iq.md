# create_entity

Create a resource only when authorized by the original request and supported
by the current native descriptor/path contract. Use the exact parentUrl and
jsonBody type (object or encoded string) the tool advertises. Inspect the
create schema when needed; do not add/delete annotations or reshape wrappers
based on this example. Native arguments are forwarded unchanged.

Conditional examples:

- Folder under `/drives/{driveId}/items/{parentId}/children`:
  a name plus `folder: {}` when supported.
- Teams message under `/chats/{chatId}/messages` or
  `/teams/{teamId}/channels/{channelId}/messages`:
  a body with contentType and the exact requested content.
- Channel reply under the exact root-message `/replies` collection.

An existing-chat-only instruction does not authorize POST `/chats`; chat
creation/reuse can create a resource. Load `teams` for exact participant,
chat/channel and message distinctions. Do not expand a person mention into
an unrelated recipient/group or infer notification delivery from acceptance.

For an explicit 1:1 message, `parentUrl` must use the actual returned chat
whose `oneOnOne` type and complete typed member userIds match exactly `/me`
and the resolved requested counterpart. The invoking requester/current chat
or channel is not a substitute. Never infer members from an opaque chat ID.
`mentioned.user.id` does not select the delivery conversation; a correct
mention in a different chat is still a wrong-destination send. If identity,
roster or required native mention support is unresolved, stop without sending.
Authorized supported chat creation must use those same two resolved people.
The returned message and established roster, not its text or mention alone,
ground any recipient claim. No automatic resend/delete to repair a mistake.

Direct selected writes execute immediately; private native writes need the
existing job approval, not a new per-tool confirmation. Legacy read-only
jobs cannot write. Inspect returned identity/status and report partial/unknown
effects honestly. No automatic retry after uncertain execution.
