# upload_blob and native transfer limits

This reference does not register upload_blob. Inspect the actual eligible
catalog: historical descriptions of targetUrl plus local filePath do not
give this host local-file access or an implemented transfer. Never invoke an
unoffered alias or read a local path supplied by a recipe.

Missing upload_blob does not prove every WorkIQ upload action is absent.
An offered native SharePoint upload_file action may have a different contract;
discover it when relevant and use only established source content and the
exact supported native argument shape. Do not manufacture attachment bytes
from metadata, summaries or model text.

Preparing an upload session or getUploadFileDetails is not transferring bytes.
If completion requires a raw PUT/POST to an external/preauthenticated URL, it
is unsupported under WorkIQ-only operation. Do not silently use direct Graph,
local HTTP, chunk transfer or a temporary file fallback. Existing slash tests
with generated text are not arbitrary original-file transfer.

Preserve explicit conflict intent. No-overwrite, automatic rename and pinned
existing-item replacement differ; do not invent supported conflict semantics
or claim exact item-ID fidelity for a folder/name alternative. Return actual
native results and uncertainty, never a fabricated file/link. Never replay
an uncertain upload or overwrite.
