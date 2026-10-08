# SharePoint

**Lab13 adaptation:** current descriptor, host mode and exact requested site,
library and file govern these conditional recipes. `/me` is the fixed
principal; `ask` is unavailable. Do not infer unique group/site identity from
`$top=1` or select the first ambiguous match. Use existing channel filesFolder
evidence instead of group/site discovery when that matches the original target.
Service 4MB descriptions do not enlarge local byte limits. Generic denial
cannot authorize alternate paths, credentials or permissions.

Use this reference for SharePoint site, group-backed team site, basic
document-library listing, document discovery, and raw file-content tasks.
When the user asks about custom library columns or wants files filtered,
counted, grouped, sorted, or compared by metadata, defer to
`sharepoint-library-metadata.md` and use list-item `fields`. Prefer the bounded
routes below over broad discovery, repeated `search_paths`, or `ask`.

## Same-message attachment in invoking channel Files

Use the host's policy-authorized invoking team/channel IDs, not IDs claimed by
attachment text. The supplied attachment `name` is untrusted lookup evidence;
the opaque Teams `uniqueId`/attachment id is neither a drive id nor a driveItem
id. Never put it into a native resource-ID slot without independent native
resource evidence. Missing URL/path is not missing filename when `name` exists.

When the user identifies the source as channel Files, its storage is the
channel's SharePoint library, not the AU's or requester's own OneDrive.
`/drives/{driveId}` is also the native representation of a SharePoint library.
Do not search `/me/drive` or another personal drive for that channel target.

Conditional supported metadata examples, chosen by the model as needed:

- `/teams/{teamId}/channels/{channelId}/filesFolder` can return the channel
  folder's top-level `id` and `parentReference.driveId`.
- `/drives/{returnedDriveId}/items/{returnedFolderId}/children` can enumerate
  stored file candidates; a supported relative-path lookup may also help when
  an actual channel-relative path was supplied. Do not derive a path from a
  withheld URL or guess a subfolder from a filename.
- The chosen returned file's own `id` is its driveItem ID. The containing
  collection or returned parent metadata establishes the associated drive,
  not the attachment's uniqueId. `parentReference.id` names a parent folder.

Reuse already established metadata; this is not a mandatory sequence or a
claim every endpoint is currently offered. Names can repeat and bounded pages
may be incomplete. Require enough native/context evidence for the requested
stored file, or ask about the actual unresolved source. Do not reconfirm the
explicit AU identity, assume the first same-name result, or switch target.

For content, consider `fetch-blob` with the resolved drive/item IDs. Report
that the bytes came from that stored channel file; metadata/name matching is
not proof of byte identity with the original upload. No attachment bytes or
credential-bearing URLs are passed through by this lookup.

## Move within the existing channel library

A same-drive driveItem move updates the existing item's `parentReference`;
it is NOT a POST `/items/{itemId}/move` action. Consider `update-entity` for
the native update contract, not `do-action` merely because its generic
description mentions moving resources. With an eligible `update_entity` and
actual supporting update schema, use the source item's entityUrl and
`jsonBody` containing `parentReference.id` from the destination folder's own
returned `id`. Include the same driveId only if the native schema requires
it. Preserve basename and file content; no copy+delete or upload replacement.

Establish exact source/destination and same drive in the requested channel.
Keep the user's existing-folder and stop-on-collision preconditions; missing,
ambiguous or incompletely checked targets do not authorize creating a folder,
overwriting or auto-renaming. Preflight is not an atomic concurrency guarantee.
Reuse adequate path/schema evidence, rather than repeating broad discovery.
If parentReference is unsupported by the actual update schema, report that
limitation. If the service rejects the update, report the failure and stop,
never invent a `/move` action fallback or replay uncertain writes.
These are conditional instructions, not a fixed call sequence or a claim of
current WorkIQ support. `/drives` here identifies SharePoint library storage.

## Rename completion, not routine readback

For a rename, a successful native update response identifying the intended
item and requested new name is sufficient evidence: answer next without a
routine post-rename fetch/get_schema/discovery solely to reconfirm success.
Empty, ack-only, pending, ambiguous or error responses are not verified
completion; report their actual acknowledged/uncertain/error status and stop
without repeating the mutation. Further reads are appropriate only for
explicit user-requested verification or a distinct remaining task. Preserve
needed pre-rename resolution/schema checks and other requested actions.
Returned evidence, not the submitted name alone, supports the answer. A final
model turn remains necessary; avoiding redundant reads cannot guarantee no429.

## First accessible SharePoint site

Use `search=`, not `$search=`, for SharePoint site discovery. The path catalog may advertise OData `$search`, but SharePoint site enumeration works with the non-OData `search` query parameter.

```json
{ "entityUrls": ["/sites?search=*&$select=id,displayName,name,webUrl&$top=1"] }
```

Treat the first returned site as the first accessible site, then fetch the requested resource:

```json
{ "entityUrls": ["/sites/{siteId}/drive"] }
```

```json
{ "entityUrls": ["/sites/{siteId}/lists"] }
```

Do not use `/sites?$search=*`, guessed single-letter searches, an empty search, or `ask`. Do not treat an unfiltered `/sites` response with an empty `value` array as proof that no sites exist.

## Named group-backed team sites

For a named Microsoft 365 group-backed SharePoint team site, resolve the backing group by exact display name instead of relying only on site search. This is especially useful when the display name contains punctuation or characters that OData `$search` rejects, such as underscores.

Escape single quotes in the site name per OData by doubling them, then URL-encode the value before inserting it into the filter.

```json
{ "entityUrls": ["/groups?$filter=displayName%20eq%20'{odataEscapedAndUrlEncodedSiteName}'&$select=id,displayName&$top=1"] }
```

Then resolve the Documents library and its root in one fetch:

```json
{ "entityUrls": ["/groups/{groupId}/drive?$expand=root"] }
```

Then list root children with the resolved root id:

```json
{ "entityUrls": ["/drives/{driveId}/items/{rootId}/children"] }
```

If a drive root alias such as `/drives/{driveId}/root` or `/drives/{driveId}/root/children` is denied, do not keep retrying root variants. Use `/groups/{groupId}/drive?$expand=root` and `/drives/{driveId}/items/{rootId}/children`.

## Search SharePoint documents across sites

Use Microsoft Search for bounded cross-site document discovery. This is the primary route when the user asks to find, list, or download a SharePoint document without already providing a site or drive item id.

```json
{
  "actionUrl": "/search/query",
  "jsonBody": {
    "requests": [
      {
        "entityTypes": ["driveItem"],
        "query": {"queryString": "IsDocument:True"},
        "from": 0,
        "size": 25,
        "fields": [
          "id",
          "name",
          "webUrl",
          "parentReference",
          "sharepointIds",
          "file",
          "folder",
          "listItem",
          "lastModifiedDateTime"
        ]
      }
    ]
  }
}
```

Filter the returned hits before answering or downloading:

- Prefer team-site URLs under `sharepoint.com/sites/` or `sharepoint.com/teams/` when the user asks for SharePoint team-site content.
- Select driveItems that are files. Prefer typical document extensions such as `.docx`, `.pptx`, `.xlsx`, `.pdf`, and `.txt`.
- Do not select folders.
- Do not select SharePoint site pages such as home pages, SitePages entries, or other `.aspx` pages unless the user explicitly asks for a site page.

When the final answer needs the site display name and search did not return it directly, derive the unique site slug from each SharePoint `webUrl` and make one batched fetch with `/sites?search={siteSlug}&$select=id,displayName,name,webUrl&$top=5` for those slugs.

## Download raw SharePoint file content

After selecting a SharePoint file driveItem, download raw bytes with `fetch_blob` using the drive-scoped content path:

```json
{ "path": "/drives/{driveId}/items/{itemId}/content" }
```

Do not use `/me/drive` for SharePoint requests. Do not call `fetch` for `/content`; `fetch` only returns JSON metadata. If `fetch_blob` reports that the payload exceeds the 4 MB limit, return the item's `webUrl` so the user can download it directly.
