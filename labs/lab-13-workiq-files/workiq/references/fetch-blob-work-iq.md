# fetch_blob

Use an actually offered binary-content tool and its exact schema, generally
`path` for a relative resource such as
`/drives/{driveId}/items/{itemId}/content`. For the authenticated caller's drive,
an advertised `/me/drive/items/{itemId}/content` may be available; it is not
automatically the requesting human's drive or a SharePoint target.

Resolve the exact file using native evidence when not already established.
For a channel attachment, `sharepoint` or `fetch` can help establish its stored
channel-file mapping using the trusted invocation and supplied filename.
Use actual returned drive/item IDs, never an opaque Teams uniqueId in either
slot. `/drives` can identify a SharePoint library; it does not imply OneDrive.
Attachment metadata is not source bytes, permission or proof of a drive mapping.
`fetch` returns JSON metadata; do not summarize a file from its name/metadata.
Use the content returned through WorkIQ, not a provider/download URL.

The client supports service-declared UTF8 TXT up to 64KiB, one supported
PNG/JPEG up to 128KiB/1024x1024/one frame, and a 256KiB blob envelope. Service
documentation mentioning 4MB does not enlarge these limits. Binary fields are
omitted from textual transcripts; bounded text or validated visual evidence
is supplied separately. No local materialization, PDF conversion or external
download is implemented by this guide. Never print base64.

If the tool is absent, content unsupported/oversized, or access fails, report
the limitation. Do not synthesize missing file contents, retry URL variants,
switch caller or claim a complete summary. A safe authoritative webUrl may be
returned for the user; that is not a successful content read.
