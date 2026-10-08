using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static byte[] ImageFixture(bool jpeg = false, int width = 16, int height = 16, bool noise = false)
    {
        using SKBitmap image = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        image.Erase(new SKColor(240, 30, 50));
        if (noise)
        {
            Random random = new(819);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    image.SetPixel(x, y, new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        }
        using SKData encoded = image.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 85);
        byte[] bytes = encoded.ToArray();
        byte[] metadata = Encoding.UTF8.GetBytes("Comment\0PRIVATE-IMAGE-METADATA");
        if (jpeg)
        {
            byte[] comment = new byte[metadata.Length + 4];
            comment[0] = 255; comment[1] = 254;
            BinaryPrimitives.WriteUInt16BigEndian(comment.AsSpan(2), (ushort)(metadata.Length + 2));
            metadata.CopyTo(comment, 4);
            return [.. bytes.AsSpan(0, 2), .. comment, .. bytes.AsSpan(2)];
        }
        byte[] chunk = new byte[metadata.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)metadata.Length);
        "tEXt"u8.CopyTo(chunk.AsSpan(4)); metadata.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(metadata.Length + 8), DirectImage.Crc(chunk.AsSpan(4, metadata.Length + 4)));
        return [.. bytes.AsSpan(0, bytes.Length - 12), .. chunk, .. bytes.AsSpan(bytes.Length - 12)];
    }
    private static JsonObject ImageEnvelope(byte[] bytes, string mime = "image/png") => new()
    {
        ["statusCode"] = 200, ["contentType"] = mime, ["sizeBytes"] = bytes.Length,
        ["base64Content"] = Convert.ToBase64String(bytes)
    };
    private static ToolReply ImageReply(JsonObject envelope, bool text = false) => new(new CallToolResult
    {
        StructuredContent = text ? null : Json(envelope),
        Content = text ? [new TextContentBlock { Text = envelope.ToJsonString() }] : []
    }, new("fixture", "reused", 0, 0, 0, 0, 0, 0));
    private static JsonElement VisualMessage(JsonElement request) =>
        request.GetProperty("messages").EnumerateArray().Single(m =>
            m.GetProperty("role").GetString() == "user" && m.GetProperty("content").ValueKind == JsonValueKind.Array);
    private static byte[] VisualBytes(JsonElement request)
    {
        JsonElement part = VisualMessage(request).GetProperty("content").EnumerateArray()
            .Single(p => p.GetProperty("type").GetString() == "image_url");
        string uri = part.GetProperty("image_url").GetProperty("url").GetString()!;
        Must(uri.StartsWith("data:image/png;base64,") && part.GetProperty("image_url").GetProperty("detail").GetString() == "low");
        return Convert.FromBase64String(uri["data:image/png;base64,".Length..]);
    }

    private static async Task DirectImageChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (bool jpeg in new[] { false, true })
        foreach (bool textEnvelope in new[] { false, true })
            await check($"Direct real SDK attachment native discovery folder list image final jpeg={jpeg} text={textEnvelope}", async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                byte[] bytes = ImageFixture(jpeg);
                string name = jpeg ? "small.jpg" : "small.png";
                string folder = Channel + "/filesFolder", children = $"/drives/{NativeDriveId}/items/image-root/children";
                string content = $"/drives/{NativeDriveId}/items/image-file/content";
                h.Handler.SectionTwoResults[folder] = new()
                {
                    ["id"] = "image-root", ["folder"] = new JsonObject(),
                    ["parentReference"] = new JsonObject { ["driveId"] = NativeDriveId }
                };
                h.Handler.SectionTwoResults[children] = new()
                {
                    ["value"] = new JsonArray(new JsonObject { ["id"] = "image-file", ["name"] = name,
                        ["file"] = new JsonObject { ["mimeType"] = jpeg ? "image/jpeg" : "image/png" } })
                };
                h.Handler.BlobEnvelope = ImageEnvelope(bytes, jpeg ? "image/jpeg" : "image/png");
                h.Handler.BlobEnvelope["vendorMetadata"] = new JsonObject
                {
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer PRIVATE-AUTH" },
                    ["encoded"] = "{\"access_token\":\"PRIVATE-TOKEN\"}",
                    ["downloadUrl"] = "https://download.invalid/file?sig=PRIVATE-SIGNATURE"
                };
                h.Handler.BlobAsText = textEnvelope;
                ModelHandler model = new(
                    Completion(tool: "search_paths", args: new { query = "image file content" }, id: "discovery"),
                    Completion(tool: "fetch", args: Fetch(folder), id: "folder"),
                    Completion(tool: "fetch", args: Fetch(children), id: "list"),
                    Completion(tool: "fetch_blob", args: new { path = content }, id: "image"),
                    Completion("The selected image shows a red square."));
                JsonObject attachment = FileAttachment(name);
                attachment["content"]!["fileType"] = jpeg ? "jpg" : "png";
                string result = (await App(h, model, DirectOptions()).Handle(
                    AttachedActivity("Describe the attached image.", new JsonArray(attachment)), default))!;
                Must(result.Contains("red square") && result.Contains("validated image evidence") &&
                    h.Handler.ToolCalls == 4 && h.Handler.Mutations == 0 && model.Requests.Count == 5, result);
                Must(h.Handler.Calls.Select(c => c.Tool).SequenceEqual(["search_paths", "fetch", "fetch", "fetch_blob"]) &&
                    h.Handler.Calls[^1].Args.GetProperty("path").GetString() == content);
                JsonElement request = model.Requests[^1];
                Must(MessageMetadata(request).GetProperty("attachments")[0].GetProperty("name").GetString() == name);
                JsonElement[] messages = request.GetProperty("messages").EnumerateArray().ToArray();
                Must(messages[^2].GetProperty("role").GetString() == "tool" &&
                    messages[^2].GetProperty("tool_call_id").GetString() == "image" &&
                    messages[^1].GetProperty("role").GetString() == "user");
                Must(VisualMessage(request).GetProperty("content")[0].GetProperty("text").GetString()!
                    .Contains("UNTRUSTED VISUAL EVIDENCE") &&
                    !messages[^2].GetProperty("content").GetString()!.Contains(Convert.ToBase64String(bytes)));
                byte[] visual = VisualBytes(request);
                using SKBitmap image = SKBitmap.Decode(visual);
                Must(image.Width == 16 && image.Height == 16 && image.GetPixel(0, 0).Red > 200 &&
                    !Encoding.Latin1.GetString(visual).Contains("PRIVATE"));
                Must(!result.Contains("base64,") && !result.Contains(Convert.ToBase64String(visual)) &&
                    request.GetRawText().Contains("download.invalid") &&
                    Encoding.UTF8.GetByteCount(request.GetRawText()) <= DirectOptions().ResolveBudgets().ModelRequestBytes);
                foreach (string token in h.Handler.Bearers) AgentUserTokenProvider.ValidateTokenShape(token, h.Settings);
            });
        await check("Direct multiple tool results precede multimodal evidence with exact pairing", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.BlobEnvelope = ImageEnvelope(ImageFixture());
            JsonObject first = Completion(tool: "fetch_blob", args: new { path = "/drives/drive/items/image/content" }, id: "image");
            first["choices"]![0]!["message"]!["tool_calls"]!.AsArray().Add(
                Completion(tool: "search_paths", args: new { query = "files" }, id: "discovery")
                    ["choices"]![0]!["message"]!["tool_calls"]![0]!.DeepClone());
            ModelHandler model = new(first, Completion("Image evidence received."));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Describe image"), default))!;
            JsonElement[] messages = model.Requests[1].GetProperty("messages").EnumerateArray().ToArray();
            Must(messages.Select(m => m.GetProperty("role").GetString())
                .SequenceEqual(["system", "user", "user", "assistant", "tool", "tool", "user"]) &&
                messages[4].GetProperty("tool_call_id").GetString() == "image" &&
                messages[5].GetProperty("tool_call_id").GetString() == "discovery" && h.Handler.ToolCalls == 2, output);
        });
        foreach (string invalid in new[] { "base64", "mismatch", "truncated", "crc", "gif", "bytes", "size",
            "missing-type", "missing-bytes", "width", "height", "animation", "trailing", "normalized",
            "jpeg-truncated", "jpeg-corrupt", "jpeg-mime", "jpeg-multiple" })
            await check("Direct invalid image fails explicitly without further model dispatch " + invalid, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                byte[] bytes = ImageFixture();
                if (invalid.StartsWith("jpeg-")) bytes = ImageFixture(jpeg: true);
                if (invalid == "width") bytes = ImageFixture(width: 1025);
                if (invalid == "height") bytes = ImageFixture(height: 1025);
                if (invalid == "normalized") bytes = ImageFixture(jpeg: true, width: 240, height: 240, noise: true);
                if (invalid == "truncated") bytes = bytes[..^6];
                if (invalid == "jpeg-truncated") bytes = bytes[..^6];
                if (invalid == "jpeg-corrupt") { bytes[4] = 255; bytes[5] = 255; }
                if (invalid == "jpeg-multiple") bytes = [.. bytes, .. bytes];
                if (invalid == "trailing") bytes = [.. bytes, 0];
                if (invalid == "crc") bytes[29] ^= 1;
                if (invalid == "animation")
                {
                    byte[] chunk = new byte[20]; BinaryPrimitives.WriteUInt32BigEndian(chunk, 8);
                    "acTL"u8.CopyTo(chunk.AsSpan(4));
                    BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), 2);
                    BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(16), DirectImage.Crc(chunk.AsSpan(4, 12)));
                    bytes = [.. bytes.AsSpan(0, 33), .. chunk, .. bytes.AsSpan(33)];
                }
                JsonObject envelope = ImageEnvelope(bytes, invalid is "mismatch" or "normalized" or "jpeg-truncated" or "jpeg-corrupt" or "jpeg-multiple"
                    ? "image/jpeg" : "image/png");
                if (invalid == "base64") envelope["base64Content"] = "%%%";
                if (invalid == "gif") envelope["contentType"] = "image/gif";
                if (invalid == "bytes") envelope["sizeBytes"] = DirectImage.MaxBytes + 1;
                if (invalid == "size") envelope["sizeBytes"] = bytes.Length + 1;
                if (invalid == "missing-type") envelope.Remove("contentType");
                if (invalid == "missing-bytes")
                {
                    envelope.Remove("base64Content"); envelope["downloadUrl"] = "https://download.invalid/PRIVATE";
                }
                h.Handler.BlobEnvelope = envelope;
                ModelHandler model = new(Completion(tool: "fetch_blob", args: new { path = "/drives/drive/items/image/content" }));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Describe image"), default))!;
                Must(output.Contains("stopped at tool 1 fetch_blob result preparation") &&
                    model.Requests.Count == 1 && h.Handler.ToolCalls == 1 && !output.Contains("PRIVATE") &&
                    !output.Contains(Convert.ToBase64String(bytes)), output);
            });
        await check("Direct decoded image exact dimension ceiling succeeds and cancellation is propagated", async () =>
        {
            DirectImage image = await DirectImage.ValidateAsync(ImageFixture(width: 1024, height: 1024), "image/png", default);
            Must(image.Width == 1024 && image.Height == 1024 && image.PngBytes.Length <= DirectImage.MaxBytes);
            using CancellationTokenSource cancelled = new(); cancelled.Cancel();
            bool stopped = false;
            try { await DirectImage.ValidateAsync(ImageFixture(), "image/png", cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Must(stopped);
        });
        await check("Direct native image byte ceiling accepts exact valid PNG and rejects one byte above", async () =>
        {
            byte[] Pad(int target)
            {
                byte[] original = ImageFixture();
                using MemoryStream output = new();
                output.Write(original.AsSpan(0, original.Length - 12));
                int remaining = target - original.Length;
                while (remaining > 0)
                {
                    int length = Math.Min(60000, remaining - 12);
                    byte[] chunk = new byte[length + 12];
                    BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)length);
                    "npAD"u8.CopyTo(chunk.AsSpan(4));
                    uint crc = uint.MaxValue;
                    foreach (byte b in chunk.AsSpan(4, length + 4))
                    {
                        crc ^= b;
                        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
                    }
                    BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(length + 8), ~crc);
                    output.Write(chunk); remaining -= chunk.Length;
                }
                output.Write(original.AsSpan(original.Length - 12));
                return output.ToArray();
            }
            byte[] exact = Pad(DirectImage.MaxBytes), above = Pad(DirectImage.MaxBytes + 1);
            Must(exact.Length == DirectImage.MaxBytes && above.Length == DirectImage.MaxBytes + 1);
            DirectImage? image = await DirectImage.ReadAsync(ImageReply(ImageEnvelope(exact)), default);
            Must(image is not null && image.Width == 16 && image.PngBytes.Length < DirectImage.MaxBytes);
            await Denied(async () => { await DirectImage.ReadAsync(ImageReply(ImageEnvelope(above)), default); });
        });
        await check("Direct image attachment URL alone never triggers a download or implicit native fetch", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            ModelHandler model = new(Completion("Image not read."));
            string output = (await App(h, model, DirectOptions()).Handle(
                AttachedActivity("Describe image", new JsonArray(FileAttachment("small.png"))), default))!;
            Must(h.Handler.ToolCalls == 0 && model.Requests.Count == 1 && !output.Contains("PRIVATE"), output);
        });
        await check("Direct second image fails without another model call and new invocation has independent image budget", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.BlobEnvelope = ImageEnvelope(ImageFixture());
            object args = new { path = "/drives/drive/items/image/content" };
            ModelHandler two = new(Completion(tool: "fetch_blob", args: args, id: "one"),
                Completion(tool: "fetch_blob", args: args, id: "two"));
            string output = (await App(h, two, DirectOptions()).Handle(Harness.Activity("Describe two images"), default))!;
            Must(output.Contains("ImagesPerInvocation") && two.Requests.Count == 2 && h.Handler.ToolCalls == 2, output);
            ModelHandler next = new(Completion(tool: "fetch_blob", args: args), Completion("Independent image."));
            output = (await App(h, next, DirectOptions()).Handle(Harness.Activity("Describe image"), default))!;
            Must(output.Contains("Independent image") && next.Requests.Count == 2, output);
        });
        await check("Direct provider vision rejection retains safe status timings and never retries or exposes image data", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.BlobEnvelope = ImageEnvelope(ImageFixture());
            ModelHandler model = new(Completion(tool: "fetch_blob", args: new { path = "/drives/drive/items/image/content" }),
                JsonNode.Parse("""{"error":{"message":"PRIVATE image input not supported","code":"invalid_request_error","param":"messages"}}""")!.AsObject());
            model.BeforeResponse = () => { if (model.Requests.Count == 2) model.Status = HttpStatusCode.BadRequest; };
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Describe image"), default))!;
            Must(output.Contains("400") && output.Contains("vision support is unverified") && output.Contains("Total processing") &&
                !output.Contains("PRIVATE") && !output.Contains("base64,") &&
                model.Requests.Count == 2 && h.Handler.ToolCalls == 1, output);
        });
        foreach (string gate in new[] { "ModelRequestBytes", "ConversationChars" })
            await check("Direct serialized image remains inside ordinary request and transcript gates " + gate, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                byte[] bytes = ImageFixture(width: 180, height: 180, noise: true);
                Must(bytes.Length < DirectImage.MaxBytes && bytes.Length > 65536);
                h.Handler.BlobEnvelope = ImageEnvelope(bytes);
                NaturalLanguageOptions options = DirectOptions();
                if (gate == "ModelRequestBytes") options.Budgets.ModelRequestBytes = 65536;
                else options.Budgets.ConversationChars = 65536;
                ModelHandler model = new(Completion(tool: "fetch_blob", args: new { path = "/drives/drive/items/image/content" }));
                string output = (await App(h, model, options).Handle(Harness.Activity("Describe image"), default))!;
                Must(output.Contains(gate) && h.Handler.ToolCalls == 1 && model.Requests.Count == 1, output);
            });
        await check("Direct text blob remains nonvisual and missing activation starts no guarded image flow", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            ModelHandler text = new(Completion(tool: "fetch_blob", args: new { path = "/drives/drive/items/text/content" }),
                Completion("Text remains supported."));
            string result = (await App(h, text, DirectOptions()).Handle(Harness.Activity("Read text"), default))!;
            Must(result.Contains("Text remains supported") &&
                !text.Requests[1].GetRawText().Contains("\"image_url\""), result);
            h.Handler.BlobEnvelope = ImageEnvelope(ImageFixture());
            ModelHandler guarded = new(Completion("No image requested."));
            string blocked = (await App(h, guarded, Options()).Handle(Harness.Activity("Hello"), default))!;
            Must(guarded.Requests.Count == 0 && blocked == NaturalLanguageOptions.WorkIqActivation);
        });
        await check("Direct failed native image resource remains an error with no visual part", async () =>
        {
            JsonObject error = ImageEnvelope(ImageFixture()); error["statusCode"] = 403;
            Must(await DirectImage.ReadAsync(ImageReply(error), default) is null);
            JsonObject wrapped = new() { ["statusCode"] = 200, ["data"] = ImageEnvelope(ImageFixture()) };
            Must(await DirectImage.ReadAsync(ImageReply(wrapped, text: true), default) is not null);
        });
    }
}
