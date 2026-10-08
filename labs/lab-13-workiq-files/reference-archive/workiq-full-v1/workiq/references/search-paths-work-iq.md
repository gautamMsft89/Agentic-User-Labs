# search_paths

Discover available paths using the CURRENT advertised input schema. Some
descriptors expose regex `filter`, others `query`. The regex examples below
apply only to a descriptor that advertises that contract. Guarded mode further
restricts discovery to an approved concrete prefix. Reuse compatible returned
paths; do not discover a known target again.

## Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filter` or `query` | Per descriptor | Per descriptor | Use only the advertised key and semantics. Never substitute one for the other from memory. Do not enumerate everything for a scoped request. |

> Discover only what the actual catalog exposes. Do not invent backend/source/
> provider parameters. Do not assert that WorkIQ is Graph-only: this lab also
> has separate native SharePoint action contracts, and Business Applications
> reference paths are not proof that this principal can use them.

## Workflow

1. `search_paths` with the narrowest supported query/filter for the requested unknown contract
2. `get_schema` on the chosen path
3. `fetch` or the appropriate write tool (`create_entity` / `update_entity` / `delete_entity` / `do_action` / `call_function`)

If the user asks to discover paths AND read or mutate, continue to the mutation tool after picking the path — discovery alone is incomplete.

Never answer API/path questions from general Graph knowledge, local SQL, filesystem search, or built-in tools. Summarize paths from `search_paths`; if none matched, say WorkIQ did not confirm one.

## Examples

### Find all message-related paths
```json
{ "filter": "messages" }
```

When the user asks what paths are available, enumerate every confirmed path
family and operation returned by that `search_paths` call rather than selecting
only the most common examples. Group related results for readability, such as
chat messages, channel messages, replies, actions, retained or pinned
messages, by-ID routes, and hosted content. Do not invent paths absent from the
result, but do not omit less common confirmed variants.

### Find calendar paths
```json
{ "filter": ".*calendar.*" }
```

### Enumerate every path
```json
{ "filter": ".*" }
```

### Find Planner paths
```json
{ "filter": "planner" }
```

### Find OneDrive/files paths
```json
{ "filter": "drive" }
```
