# get_schema

Inspect the supported operation's shape when the current descriptor/evidence
is insufficient. Use the exact offered `path`, `operationType` and `format`
contract; advertised formats can include jsonschema, TypeScript, CDDL or YAML.
Do not invent method, verb, backend, API-version or response-mode selectors.
Reuse a compatible returned schema instead of requesting multiple formats.

Where advertised, collection reads use `fetch`, collection POST uses `create`,
item PATCH uses `update`, and action verbs use `action`. Examples:

- `/drives/{driveId}/items/{folderId}/children`: read or folder-create schema.
- `/teams/{teamId}/channels/{channelId}/messages`: read or message-create schema.
- `/drives/{driveId}/items/{itemId}`: metadata read or update schema.
- `/drives/{driveId}/items/{itemId}/createUploadSession`: action request schema.

Write schemas describe REQUEST bodies, not action response resources. A
createUploadSession request schema does not establish uploadUrl, expiration
or nextExpectedRanges response semantics. Report that limitation rather than
inventing a response-schema selector or looking up unrelated entity schemas.

Schema availability is not permission or proof of operation support. For
example, presence metadata may expose writable-looking fields even though
the service uses explicit actions instead of PATCH. Preserve native errors;
do not repair annotations/body wrappers or switch versions to match examples.
