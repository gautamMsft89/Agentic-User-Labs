# delete_entity

**Lab13 adaptation:** this is a native-write reference, unavailable in guarded
and legacy private-read modes. Ordinary Direct and per-job-approved private
native use their existing authorization, never a new universal confirmation
rule. Preserve exact targets and never replay an uncertain deletion.

DELETE a WorkIQ entity. Permanent — use with care, especially for emails and calendar events.

## Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `entityUrl` | string | Yes | Entity path including ID (`/me/events/{id}`). Server-relative, starts with `/`, no scheme. URL-encode special characters. |
| `headers` | unavailable here | — | Header overrides are blocked. A required If-Match that cannot be supplied through the supported contract makes this operation unsupported; never drop the precondition. |

## When to Use

- Delete a calendar event
- Delete a draft email
- Remove a Planner task
- Delete a Teams message (where permitted)

## Gotchas

- **Email delete moves to Deleted Items** — that's the right default for any "delete / remove / get rid of this email" request. Reach for `do_action` with `/me/messages/{id}/permanentDelete` only when the user explicitly asks for permanent, unrecoverable removal, and only against the **single resolved message ID** — never loop `permanentDelete` across a list of messages.
- **Event delete** sends cancellation notices if it was an organized meeting.
- **A Teams chat is not deleted from the user's chat list with this tool.** For
  "delete", "remove", or "hide" a chat from my list, resolve the exact chat and
  call `do_action` on `/chats/{chatId}/hideForUser` with the signed-in user's
  `teamworkUserIdentity`. This is a per-user hide and does not delete the chat
  for other participants.
- Confirm the entity ID with `fetch` before deleting.

## Workflow

1. `fetch` to confirm the correct entity and ID
2. `delete_entity` with the entity's full path including ID

## Examples

### Delete a calendar event
```json
{ "entityUrl": "/me/events/{id}" }
```

### Delete a draft email
```json
{ "entityUrl": "/me/messages/{id}" }
```

### Delete a Planner task
```json
{ "entityUrl": "/planner/tasks/{taskId}" }
```
