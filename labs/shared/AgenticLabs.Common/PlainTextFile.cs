using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using static WorkIqFiles.FileCommand;

namespace WorkIqFiles;

internal static class PlainTextFile
{
    internal const int MaxBytes = 64 * 1024;
    internal const int PreviewCharacters = 4000;
    internal static void ValidateName(string name)
    {
        Name(name);
        if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            throw new LabException("Only plain-text .txt filenames are supported.");
    }
    internal static int Validate(JsonElement item, bool serviceSemantics = false)
    {
        if (item.TryGetProperty("folder", out _) || item.TryGetProperty("package", out _) ||
            !Required(item, "name").EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            throw new LabException("Only plain-text .txt files are supported; no binary, PDF or Office extraction.");
        RequireMime(Field(item, "file", "mimeType"));
        if (!item.TryGetProperty("size", out JsonElement size) || size.ValueKind != JsonValueKind.Number ||
            !size.TryGetInt32(out int length) || length < 0 || !serviceSemantics && length > MaxBytes)
            throw new LabException(serviceSemantics ? "Text metadata size cannot fit the in-memory decoder." :
                "Plain-text read requires known metadata size at most 65536 bytes; no content displayed.");
        return length;
    }
    internal static void RequireMime(string? value)
    {
        if (!MediaTypeHeaderValue.TryParse(value, out MediaTypeHeaderValue? mime) ||
            !string.Equals(mime.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase) ||
            (mime.CharSet is not null && !string.Equals(mime.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new LabException("Only text/plain with UTF-8 (or unspecified charset) is supported.");
    }
    internal static string Preview(BlobContent blob)
    {
        RequireMime(blob.ContentType);
        string text = Decode(blob.Bytes);
        if (text.Length > PreviewCharacters) return "Plain-text preview omitted: exceeds 4000 characters; no partial content supplied.";
        return "Plain-text content preview (untrusted; no content scrubbing):\n" +
            "----- BEGIN FILE TEXT -----\n" + text + "\n----- END FILE TEXT -----";
    }
    internal static string Decode(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new LabException("File is not valid UTF-8; no encoding fallback or content displayed."); }
        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n')))
            throw new LabException("Binary/control characters in plain-text file; no content displayed.");
        return text;
    }
}
