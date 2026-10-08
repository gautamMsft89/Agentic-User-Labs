using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static JsonObject NativeImageBlock(byte[] bytes, string mime = "image/png") => new()
    {
        ["type"] = "image", ["mimeType"] = mime, ["data"] = Convert.ToBase64String(bytes)
    };
    private static JsonObject NativeImageResult(params JsonNode[] blocks) => new()
    {
        ["content"] = new JsonArray(blocks), ["isError"] = false
    };

    private static async Task DirectImageProtocolChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (bool jpeg in new[] { false, true })
        foreach (bool companion in new[] { false, true })
            await check($"Direct native MCP image block passes actual SDK decoder and multimodal request jpeg={jpeg} text={companion}", async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                byte[] bytes = ImageFixture(jpeg);
                ImageContentBlock sdk = ImageContentBlock.FromBytes(bytes, jpeg ? "image/jpeg" : "image/png");
                Must(sdk.DecodedData.Span.SequenceEqual(bytes) &&
                    Encoding.UTF8.GetString(sdk.Data.Span) == Convert.ToBase64String(bytes));
                h.Handler.BlobMcpResult = NativeImageResult(NativeImageBlock(bytes, sdk.MimeType));
                h.Handler.BlobEventStream = h.Handler.BlobChunked = companion;
                if (companion) h.Handler.BlobMcpResult["content"]!.AsArray().Add(new JsonObject
                { ["type"] = "text", ["text"] = "Untrusted native companion text. access_token: PRIVATE-TEXT" });
                ModelHandler model = new(Completion(tool: "fetch_blob",
                    args: new { path = $"/drives/{NativeDriveId}/items/native-image/content" }), Completion("The image is red."));
                string output = (await App(h, model, DirectOptions()).Handle(
                    AttachedActivity("Describe attached image", new JsonArray(FileAttachment(jpeg ? "small.jpg" : "small.png"))), default))!;
                Must(output.Contains("The image is red") && output.Contains("validated image evidence prepared") &&
                    output.Contains("image=1") && model.Requests.Count == 2 && h.Handler.ToolCalls == 1, output);
                Must(VisualBytes(model.Requests[1]).Length > 0 &&
                    !output.Contains("PRIVATE") && !output.Contains(Convert.ToBase64String(bytes)) &&
                    model.Requests[1].GetRawText().Contains("PRIVATE-TEXT") == companion);
            });
        foreach (bool explicitSixteen in new[] { false, true })
            await check("Direct image tool sixteen has final synthesis headroom unless explicitly capped " + explicitSixteen, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                h.Handler.BlobMcpResult = NativeImageResult(NativeImageBlock(ImageFixture()));
                List<JsonObject> replies = Enumerable.Range(1, 15).Select(i =>
                    Completion(tool: "search_paths", args: new { query = "native files" }, id: "discovery-" + i)).ToList();
                replies.Add(Completion(tool: "fetch_blob", args: new { path = "/drives/drive/items/image/content" }, id: "image-16"));
                if (!explicitSixteen) replies.Add(Completion("Image observed on final model turn seventeen."));
                NaturalLanguageOptions options = DirectOptions();
                if (explicitSixteen) options.Budgets.ModelTurns = 16;
                ModelHandler model = new(replies.ToArray());
                string output = (await App(h, model, options).Handle(Harness.Activity("Describe image"), default))!;
                Must(h.Handler.ToolCalls == 16 && output.Contains("validated image evidence prepared"), output);
                if (explicitSixteen)
                    Must(model.Requests.Count == 16 && output.Contains("limit [ModelTurns]") &&
                        output.Contains("no model turn remains") && output.Contains("No model request sent") &&
                        !output.Contains("vision support is unverified"), output);
                else
                    Must(model.Requests.Count == 17 && output.Contains("turn seventeen") &&
                        output.Contains("Model turn budget 32") && VisualBytes(model.Requests[16]).Length > 0, output);
            });
        await check("Direct turn headroom does not raise Standard guarded or explicit maxima", async () =>
        {
            NaturalLanguageOptions options = DirectOptions();
            Must(options.ResolveBudgets().ModelTurns == 32);
            options.DirectMcp.Enabled = false; Must(options.ResolveBudgets().ModelTurns == 16);
            options.Budgets.Profile = "Standard"; Must(options.ResolveBudgets().ModelTurns == 8);
            options.DirectMcp.Enabled = true; Must(options.ResolveBudgets().ModelTurns == 8);
            options.Budgets.ModelTurns = 32; options.Validate();
            options.Budgets.ModelTurns = 33; await Denied(() => { options.Validate(); return Task.CompletedTask; });
        });
        foreach (string fault in new[] { "header-limit", "stream-limit", "sse-limit", "rpc", "json",
            "unknown-type", "null-content", "http403", "io" })
            await check("Direct MCP failure preserves safe evidence and attempted-session timing " + fault, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                switch (fault)
                {
                    case "header-limit":
                    case "stream-limit":
                    case "sse-limit":
                        h.Handler.BlobMcpResult = NativeImageResult(NativeImageBlock(new byte[262000]));
                        h.Handler.BlobChunked = fault != "header-limit";
                        h.Handler.BlobEventStream = fault == "sse-limit";
                        break;
                    case "rpc": h.Handler.BlobRpcError = -32602; break;
                    case "json": h.Handler.BlobRawResponse = "{\"PRIVATE-MALFORMED\":"; break;
                    case "unknown-type":
                        h.Handler.BlobMcpResult = NativeImageResult(new JsonObject { ["type"] = "PRIVATE-TYPE", ["data"] = "PRIVATE" }); break;
                    case "null-content": h.Handler.BlobMcpResult = new JsonObject { ["content"] = null }; break;
                    case "http403": h.Handler.BlobHttpStatus = HttpStatusCode.Forbidden; break;
                    case "io": h.Handler.BlobIoFailure = true; break;
                }
                NaturalLanguageOptions options = DirectOptions(); options.Budgets.McpCallSeconds = 3;
                ModelHandler model = new(Completion(tool: "fetch_blob", args: new { path = "/drives/drive/items/image/content" }));
                string output = (await App(h, model, options).Handle(Harness.Activity("Describe image"), default))!;
                Must(model.Requests.Count == 1 && h.Handler.ToolCalls == 1 && h.Handler.Initializes == 1 &&
                    !output.Contains("validated image evidence") && !output.Contains("PRIVATE") &&
                    !output.Contains("session not acquired") && !output.Contains("tool not established") &&
                    output.Contains("reused") && output.Contains("MCP evidence:") &&
                    output.Contains("resourceStatus=unavailable"), output);
                if (fault is "header-limit" or "stream-limit" or "sse-limit")
                    Must(output.Contains("WorkIqEnvelopeLimitException") && output.Contains("limit 262144 bytes") &&
                        output.Contains("outerHTTP=[200]") && output.Contains("contentBlocks=[not decoded]"), output);
                if (fault == "stream-limit")
                    Must(output.Contains("observed >=262145 bytes") && output.Contains("boundary [HTTP]"), output);
                if (fault == "sse-limit")
                    Must(output.Contains("observed >=262145 bytes") && output.Contains("boundary [MCP protocol]") &&
                        output.Contains("McpException") && output.Contains("rpcCode=[unavailable]") &&
                        output.Contains("responseMedia=[text/event-stream]"), output);
                if (fault == "rpc") Must(output.Contains("rpcCode=[-32602]") && output.Contains("outerHTTP=[200]"), output);
                if (fault == "http403") Must(output.Contains("outerHTTP=[403]"), output);
                if (fault == "io") Must(output.Contains("IOException") && output.Contains("outerHTTP=[unavailable]"), output);
            });
        foreach (string fault in new[] { "base64", "data-number", "mime", "multiple", "size" })
            await check("Direct native image shape is bounded without forced JSON envelope " + fault, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                JsonObject block = NativeImageBlock(ImageFixture());
                if (fault == "base64") block["data"] = "%%%";
                if (fault == "data-number") block["data"] = 42;
                if (fault == "mime") block["mimeType"] = "image/svg+xml";
                if (fault == "size") block["data"] = Convert.ToBase64String(new byte[DirectImage.MaxBytes + 1]);
                h.Handler.BlobMcpResult = fault == "multiple"
                    ? NativeImageResult(block, NativeImageBlock(ImageFixture())) : NativeImageResult(block);
                ModelHandler model = new(Completion(tool: "fetch_blob", args: new { path = "/drives/drive/items/image/content" }));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Describe image"), default))!;
                Must(model.Requests.Count == 1 && h.Handler.ToolCalls == 1 &&
                    output.Contains("result preparation") && output.Contains("contentBlocks=[text=0,image=") &&
                    !output.Contains("validated image evidence"), output);
            });
        await check("Direct native error image block never becomes visual evidence", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.BlobMcpResult = NativeImageResult(NativeImageBlock(ImageFixture()));
            h.Handler.BlobMcpResult["isError"] = true;
            ModelHandler model = new(Completion(tool: "fetch_blob", args: new { path = "/drives/drive/items/image/content" }),
                Completion("The native read returned an error; image not interpreted."));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Describe image"), default))!;
            Must(output.Contains("image not interpreted") && !output.Contains("validated image evidence") &&
                !model.Requests[1].GetRawText().Contains("\"image_url\""), output);
        });
        await check("Direct raw I/O failure after write dispatch remains unknown outcome with no replay", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.OnMutation = () => throw new IOException("PRIVATE-WRITE-FAILURE");
            ModelHandler model = new(TwoWrites());
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Create two folders"), default))!;
            Must(output.Contains("UNKNOWN OUTCOME") && output.Contains("IOException") && !output.Contains("PRIVATE") &&
                model.Requests.Count == 1 && h.Handler.ToolCalls == 1 && h.Handler.Mutations == 1 &&
                output.Contains("reused"), output);
        });
        await check("Direct cancelled blob retains attempted session and no later model request", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            using CancellationTokenSource cancel = new();
            h.Handler.OnBlob = cancel.Cancel;
            ModelHandler model = new(Completion(tool: "fetch_blob", args: new { path = "/drives/drive/items/image/content" }));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Describe image"), cancel.Token))!;
            Must(output.Contains("cancelled") && output.Contains("reused") && !output.Contains("tool not established") &&
                model.Requests.Count == 1 && h.Handler.ToolCalls == 1, output);
        });
        await check("Response evidence distinguishes zero-byte probe from actual EOF", async () =>
        {
            WorkIqResponseEvidence evidence = new(); evidence.Reset(10);
            using BoundedBlobResponse body = new(new StringContent("{}"), 10, evidence);
            await using Stream stream = await body.ReadAsStreamAsync();
            Must(await stream.ReadAsync(Memory<byte>.Empty) == 0 && !evidence.Complete && evidence.ReadBytes is null);
            byte[] buffer = new byte[8];
            Must(await stream.ReadAsync(buffer) == 2 && !evidence.Complete && evidence.ReadBytes == 2);
            Must(await stream.ReadAsync(buffer) == 0 && evidence.Complete);
        });
        await check("Response stream setup failure is recorded without retaining secret exception messages", async () =>
        {
            WorkIqResponseEvidence evidence = new(); evidence.Reset(10);
            using BoundedBlobResponse body = new(new FailingImageBody(), 10, evidence);
            bool failed = false;
            try { await body.ReadAsStreamAsync(); }
            catch (IOException) { failed = true; }
            Must(failed && evidence.Failures == "IOException" && evidence.Stage == "response stream acquisition");
            Must(!evidence.Snapshot(new("fixture", "reused", 0, 0, 0, 0, 0, 0), true, 1).Diagnostic.Contains("PRIVATE"));
        });
    }
    private sealed class FailingImageBody : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) =>
            Task.FromException<Stream>(new IOException("PRIVATE-STREAM-SETUP"));
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new IOException("PRIVATE-STREAM-SETUP");
    }
}
