using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static readonly string NativeDriveId = "b!" +
        Convert.ToBase64String(Enumerable.Range(0, 48).Select(i => (byte)(i * 5)).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ToolReply NativeReply(JsonObject data) => new(new CallToolResult
    {
        StructuredContent = Json(data), Content = []
    }, new("fixture", "reused", 0, 0, 0, 0, 0, 0));

    private static async Task DirectResourceIdChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (bool encoded in new[] { false, true })
            await check("Direct typed drive ID survives filesFolder listing content SDK chain " + encoded, async () =>
            {
                await using Harness h = new(); DirectEnable(h); h.Handler.SectionTwoText = encoded;
                const string folder = "01SYNTHETICFOLDER", file = "01SYNTHETICFILE";
                string folderPath = Channel + "/filesFolder?$select=id,name,webUrl,folder,parentReference";
                string childrenPath = $"/drives/{NativeDriveId}/items/{folder}/children?$select=id,name,file,folder,parentReference&$top=50";
                string contentPath = $"/drives/{NativeDriveId}/items/{file}/content";
                h.Handler.SectionTwoResults[folderPath] = new()
                {
                    ["id"] = folder, ["name"] = "Documents", ["folder"] = new JsonObject(),
                    ["parentReference"] = new JsonObject { ["driveId"] = NativeDriveId },
                    ["webUrl"] = "https://synthetic.invalid/PRIVATE-LINK"
                };
                h.Handler.SectionTwoResults[childrenPath] = new()
                {
                    ["value"] = new JsonArray(new JsonObject
                    {
                        ["id"] = file, ["name"] = "sample2.txt",
                        ["file"] = new JsonObject { ["mimeType"] = "text/plain" },
                        ["parentReference"] = new JsonObject { ["driveId"] = NativeDriveId, ["id"] = folder }
                    })
                };
                h.Handler.BlobBytes = Encoding.UTF8.GetBytes("Synthetic sample two content.");
                ModelHandler model = new(
                    Completion(tool: "fetch", args: Fetch(folderPath), id: "folder"),
                    Completion(tool: "fetch", args: Fetch(childrenPath), id: "children"),
                    Completion(tool: "fetch_blob", args: new { path = contentPath }, id: "text"),
                    Completion("Sample two contains synthetic content."));
                model.BeforeResponse = () =>
                {
                    if (model.Requests.Count != 2) return;
                    string result = model.Requests[1].GetProperty("messages").EnumerateArray().Last()
                        .GetProperty("content").GetString()!;
                    JsonElement data = NativeData(result);
                    Must(data.GetProperty("results")[0].GetProperty("data")
                        .GetProperty("parentReference").GetProperty("driveId").GetString() == NativeDriveId,
                        "Model must receive the complete service identifier before selecting children.");
                };
                string output = (await App(h, model, DirectOptions()).Handle(
                    Harness.Activity("Find sample2.txt in this channel's files, read its contents, and summarize it."), default))!;
                Must(output.Contains("Sample two contains synthetic content.") && output.Contains(NativeDriveId) &&
                    !output.Contains("PRIVATE-LINK") && h.Handler.ToolCalls == 3 && h.Handler.Mutations == 0, output);
                Must(h.Handler.Calls[1].Args.GetProperty("entityUrls")[0].GetString() == childrenPath &&
                    h.Handler.Calls[2].Args.GetProperty("path").GetString() == contentPath);
                Must(model.Requests[3].GetProperty("messages").EnumerateArray().Last()
                    .GetProperty("content").GetString()!.Contains("Synthetic sample two content."));
            });
        await check("Direct native fields retain supplied content without opaque heuristics", () =>
        {
            Must(DirectMcpPresentation.Text(NativeDriveId, 512000) == NativeDriveId);
            string opaque = new('Q', 88);
            string output = DirectMcpPresentation.Result(NativeReply(new()
            {
                ["id"] = NativeDriveId,
                ["parentReference"] = new JsonObject { ["driveId"] = NativeDriveId },
                ["dynamic"] = new JsonObject
                {
                    ["unknownValue"] = opaque, ["untypedDrive"] = NativeDriveId,
                    ["credential"] = new JsonObject { ["driveId"] = NativeDriveId },
                    ["encoded"] = JsonSerializer.Serialize(new
                    {
                        headers = new { id = NativeDriveId },
                        accessToken = opaque, driveId = NativeDriveId
                    })
                },
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer PRIVATE-AUTH", ["id"] = NativeDriveId },
                ["secret"] = NativeDriveId, ["bytes"] = NativeDriveId,
                ["@microsoft.graph.downloadUrl"] = "https://synthetic.invalid/PRIVATE?sig=" + opaque,
                ["message"] = "access_token: PRIVATE-EMBEDDED",
                ["webUrl"] = "https://synthetic.invalid/PRIVATE?sig=" + opaque
            }), DirectOptions().ResolveBudgets());
            using JsonDocument doc = JsonDocument.Parse(output.Split('\n', 2)[1]);
            JsonElement data = doc.RootElement;
            using JsonDocument encodedData = JsonDocument.Parse(data.GetProperty("dynamic").GetProperty("encoded").GetString()!);
            Must(data.GetProperty("id").GetString() == NativeDriveId &&
                data.GetProperty("parentReference").GetProperty("driveId").GetString() == NativeDriveId &&
                encodedData.RootElement.GetProperty("driveId").GetString() == NativeDriveId);
            Must(data.GetProperty("secret").GetString() == NativeDriveId &&
                data.GetProperty("bytes").GetString() != NativeDriveId &&
                data.GetProperty("dynamic").GetProperty("untypedDrive").GetString() == NativeDriveId &&
                output.Contains("PRIVATE") && output.Contains(opaque) && output.Contains("https://"));
            return Task.CompletedTask;
        });
        foreach (string value in new[]
        {
            "Bearer PRIVATE-AUTH", "https://synthetic.invalid/PRIVATE?sig=SECRET",
            "eyJ" + new string('A', 80) + "." + new string('B', 80) + "." + new string('C', 80),
            NativeDriveId + "\naccess_token: PRIVATE-SECRET",
            new string('D', 88), "b!" + new string('E', 129)
        })
            await check("Direct native field content is not scrubbed based on spelling", () =>
            {
                string output = DirectMcpPresentation.Result(NativeReply(new() { ["driveId"] = value }),
                    DirectOptions().ResolveBudgets());
                Must(NativeData(output).GetProperty("driveId").GetString() == value, output);
                return Task.CompletedTask;
            });
        foreach (string key in new[] { "entityUrls", "entityUrl", "parentUrl", "actionUrl", "path" })
            await check("Direct selected path display keeps exact supplied query " + key, () =>
            {
                string route = "/drives/" + NativeDriveId + "/items/file/content?access_token=PRIVATE-QUERY";
                object args = key == "entityUrls" ? new Dictionary<string, object?> { [key] = new[] { route } } :
                    new Dictionary<string, object?> { [key] = route };
                string trace = DirectMcpPresentation.Arguments(Json(args), 12000);
                Must(trace.Contains(NativeDriveId) && trace.Contains("PRIVATE-QUERY"), trace);
                string absolute = DirectMcpPresentation.Arguments(Json(new { path = "https://synthetic.invalid" + route }), 12000);
                Must(absolute.Contains(NativeDriveId) && absolute.Contains("PRIVATE-QUERY"), absolute);
                return Task.CompletedTask;
            });
    }
}
