using System.Text.Json;
namespace WorkIqFiles;

internal sealed record FetchResult(int Status, JsonElement Data);
internal sealed record Collection(JsonElement[] Items, bool Incomplete);
internal sealed record BlobContent(byte[] Bytes, string? ContentType);
