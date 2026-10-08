# upload_blob

> **Reference-only, unavailable in Lab13.** The entire contract below is retained
> for documentation, not invocation. Do not call this tool, read a local file,
> obtain an external transfer URL or PUT chunks. Missing upload_blob does not
> establish a universal WorkIQ upload restriction: the separate guarded slash
> synthetic-upload test uses approved generated text, not arbitrary attachments.
> Native execution still needs a proven source-to-target transfer contract;
> preparation or a URL alone does not complete an upload.

Upload a local file to a WorkIQ path via HTTP PUT. Use this to upload files to OneDrive or SharePoint.

## Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `targetUrl` | string | Yes | The target path for the upload (e.g., `/me/drive/root:/{filename}:/content`). Must be a relative path — do not include a base URL. |
| `filePath` | string | Yes | The absolute local file path to upload. |

## When to Use

- Uploading a file to OneDrive
- Uploading a file to a SharePoint document library
- Replacing the content of an existing file

## Path Conventions

| Action | Path pattern |
|--------|-------------|
| Upload to OneDrive root by filename | `/me/drive/root:/{filename}:/content` |
| Upload to a specific folder | `/me/drive/root:/{folder}/{filename}:/content` |
| Replace a file by item ID | `/me/drive/items/{id}/content` |
| Upload to SharePoint | `/drives/{driveId}/root:/{filename}:/content` |

## Gotchas

- **Historical size/transfer reference only**: the supplied contract describes
  up to 4MB and larger upload sessions. Neither PUT nor chunk transfer is
  implemented here. Never follow an uploadUrl or claim a prepared session is
  a completed file transfer.
- The URL uses the Graph path-based format `root:/{path}:/content` — include the leading `/` before the filename.

## Examples

### Upload a file to OneDrive root
```json
{
  "targetUrl": "/me/drive/root:/report.pdf:/content",
  "filePath": "C:\\Users\\user\\Documents\\report.pdf"
}
```

### Upload a file to a subfolder in OneDrive
```json
{
  "targetUrl": "/me/drive/root:/Projects/Alpha/spec.docx:/content",
  "filePath": "C:\\Users\\user\\Documents\\spec.docx"
}
```

### Replace an existing file by ID
```json
{
  "targetUrl": "/me/drive/items/{id}/content",
  "filePath": "C:\\Users\\user\\Documents\\updated-report.pdf"
}
```
