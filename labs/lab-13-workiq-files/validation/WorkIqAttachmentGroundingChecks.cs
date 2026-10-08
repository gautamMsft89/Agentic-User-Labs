using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static async Task WorkIqAttachmentGroundingChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (string type in new[] { "application/vnd.microsoft.teams.file.download.info", "application/vnd.microsoft.teams.card.file.info" })
            await check("Installed Teams attachment retains name opaque ID and trusted invocation before any reference " + type, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                JsonObject attachment = FileAttachment("Channel notes.txt");
                attachment["contentType"] = type;
                attachment["contentUrl"] = "https://synthetic.sharepoint.com/sites/site/Shared%20Documents/Channel%20notes.txt?sig=PRIVATE";
                attachment["content"]!["teamId"] = "untrusted-attachment-team";
                attachment["content"]!["channelId"] = "untrusted-attachment-channel";
                ModelHandler model = new(Completion("Metadata only; no file read."));
                await App(h, model, DirectOptions()).Handle(
                    AttachedActivity(ExplicitAuAttachmentPrompt, new JsonArray(attachment)), default);
                JsonElement request = model.Requests.Single(), metadata = MessageMetadata(request);
                string system = SystemText(request);
                Must(system.Contains("NOT WorkIQ/Graph `drive.id` or `driveItem.id`") &&
                    system.Contains("same uniqueId into both") && system.Contains("Path catalog templates") &&
                    system.Contains("Channel Files are a SharePoint document library") &&
                    system.Contains("not `/me/drive` or personal-drive search") &&
                    system.Contains($"Invoking team ID: {Harness.Team}; channel ID: {Harness.Channel}") &&
                    system.Contains("policy-authorized activity context"));
                Must(metadata.GetProperty("teamId").GetString() == Harness.Team &&
                    metadata.GetProperty("channelId").GetString() == Harness.Channel &&
                    metadata.GetProperty("activityId").GetString() == "current-message-123" &&
                    metadata.GetProperty("conversationId").GetString()!.Contains(";messageid=thread-root-789") &&
                    metadata.GetProperty("attachments")[0].GetProperty("name").GetString() == "Channel notes.txt" &&
                    metadata.GetProperty("attachments")[0].GetProperty("teamsFileUniqueId").GetString() == "1150D938-8870-4044-9F2C-5BBDEBA70C9D");
                Must(!system.Contains("Channel notes.txt") && !request.GetRawText().Contains("untrusted-attachment-team") &&
                    !request.GetRawText().Contains("untrusted-attachment-channel") && !request.GetRawText().Contains("PRIVATE"));
                JsonElement[] messages = request.GetProperty("messages").EnumerateArray().ToArray();
                Must(messages[1].GetProperty("role").GetString() == "user" &&
                    messages[1].GetProperty("content").GetString()!.StartsWith(DirectMessageContext.Label) &&
                    messages[2].GetProperty("content").GetString() == ExplicitAuAttachmentPrompt);
                JsonElement loader = request.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function"))
                    .Single(t => t.GetProperty("name").GetString() == WorkIqSkill.LoadTool);
                Must(loader.GetProperty("description").GetString()!.Contains("unresolved attachment/channel-file mapping") &&
                    loader.GetProperty("parameters").GetProperty("properties").GetProperty("reference").GetProperty("enum")
                        .EnumerateArray().Any(id => id.GetString() == "sharepoint"));
                CheckLoaded(request);
                Must(h.Handler.ToolCalls == 0);
            });

        await check("LLM-selected SharePoint and content references ground channel file IDs without native argument repair", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            string folder = Channel + "/filesFolder";
            string children = $"/drives/{NativeDriveId}/items/channel-folder/children";
            string content = $"/drives/{NativeDriveId}/items/stored-file/content";
            h.Handler.SectionTwoResults[folder] = new()
            {
                ["id"] = "channel-folder", ["folder"] = new JsonObject(),
                ["parentReference"] = new JsonObject { ["driveId"] = NativeDriveId, ["id"] = "parent-not-channel-folder" }
            };
            h.Handler.SectionTwoResults[children] = new()
            {
                ["value"] = new JsonArray(new JsonObject
                {
                    ["id"] = "stored-file", ["name"] = "Channel notes.txt", ["file"] = new JsonObject(),
                    ["parentReference"] = new JsonObject { ["driveId"] = NativeDriveId, ["id"] = "channel-folder" }
                })
            };
            h.Handler.BlobBytes = Encoding.UTF8.GetBytes("The stored channel document describes a synthetic release plan.");
            ModelHandler model = new(LoadReference("sharepoint", "mapping"),
                Completion(tool: "fetch", args: Fetch(folder), id: "folder"),
                Completion(tool: "fetch", args: Fetch(children), id: "children"),
                LoadReference("fetch-blob", "content-guidance"),
                Completion(tool: "fetch_blob", args: new { path = content }, id: "content"),
                Completion("The stored channel file describes a release plan. Metadata lookup does not prove original attachment byte identity."));
            JsonObject attachment = FileAttachment("Channel notes.txt");
            attachment["contentUrl"] = "https://synthetic.sharepoint.com/sites/site/Shared%20Documents/Channel%20notes.txt?sig=PRIVATE";
            string output = (await App(h, model, DirectOptions()).Handle(
                AttachedActivity(ExplicitAuAttachmentPrompt, new JsonArray(attachment)), default))!;
            Must(h.Handler.ToolCalls == 3 && h.Handler.Mutations == 0 && model.Requests.Count == 6 &&
                output.Contains("2 selections; 2 unique") && output.Contains("stored channel file"), output);
            (string Tool, JsonElement Args)[] expected =
            [
                ("fetch", JsonSerializer.SerializeToElement(Fetch(folder))),
                ("fetch", JsonSerializer.SerializeToElement(Fetch(children))),
                ("fetch_blob", JsonSerializer.SerializeToElement(new { path = content }))
            ];
            var wire = h.Handler.WireToolRequests.ToArray();
            for (int i = 0; i < expected.Length; i++)
            {
                JsonElement call = wire[i].GetProperty("params");
                Must(call.GetProperty("name").GetString() == expected[i].Tool &&
                    call.GetProperty("arguments").GetRawText() == expected[i].Args.GetRawText());
                Must(!call.GetRawText().Contains("/me/drive") && !call.GetRawText().Contains("1150D938") &&
                    !call.GetRawText().Contains("parent-not-channel-folder"));
            }
            foreach (JsonElement request in model.Requests)
                Must(request.GetProperty("messages")[2].GetProperty("content").GetString() == ExplicitAuAttachmentPrompt);
            CheckLoaded(model.Requests[0]); CheckLoaded(model.Requests[1], "sharepoint");
            CheckLoaded(model.Requests[5], "sharepoint", "fetch-blob");
            Must(model.Requests[5].GetProperty("messages").EnumerateArray().Any(m =>
                m.GetProperty("role").GetString() == "tool" &&
                m.GetProperty("content").GetString()!.Contains("synthetic release plan")));
        });

        foreach (bool ambiguous in new[] { false, true })
            await check("Missing or ambiguous source metadata remains model clarification without guessed ID " + ambiguous, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                JsonObject attachment = FileAttachment(); attachment.Remove("name");
                JsonArray attachments = ambiguous
                    ? new JsonArray(FileAttachment("one.txt"), FileAttachment("two.txt"))
                    : new JsonArray(attachment);
                ModelHandler model = new(Completion(ambiguous
                    ? "Which of the two attached channel files should I read?"
                    : "The attachment has no supplied filename or established native mapping. Which channel file is it?"));
                string output = (await App(h, model, DirectOptions()).Handle(
                    AttachedActivity(ExplicitAuAttachmentPrompt, attachments), default))!;
                Must(h.Handler.ToolCalls == 0 && output.Contains("Which") && output.Contains("0 selections; 0 unique"), output);
                CheckLoaded(model.Requests.Single());
                if (!ambiguous)
                    Must(MessageMetadata(model.Requests[0]).GetProperty("attachments")[0].GetProperty("name").ValueKind == JsonValueKind.Null);
            });

        await check("Erroneous scripted uniqueId proposal is not silently repaired or replaced by a forced resolver", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            const string opaque = "1150D938-8870-4044-9F2C-5BBDEBA70C9D";
            string bad = $"/drives/{opaque}/items/{opaque}?$select=id,name,driveId,webUrl,file,parentReference,size";
            h.Handler.SectionTwoResults[bad] = new()
            {
                ["statusCode"] = 400, ["error"] = new JsonObject { ["code"] = "invalidRequest" }
            };
            ModelHandler model = new(Completion(tool: "fetch", args: Fetch(bad), id: "bad-proposal"),
                Completion("The lookup failed; no file content was read."));
            string output = (await App(h, model, DirectOptions()).Handle(
                AttachedActivity(ExplicitAuAttachmentPrompt, new JsonArray(FileAttachment())), default))!;
            Must(h.Handler.ToolCalls == 1 && h.Handler.WireToolRequests.Single().GetProperty("params")
                .GetProperty("arguments").GetRawText() == JsonSerializer.SerializeToElement(Fetch(bad)).GetRawText() &&
                output.Contains("no file content was read") && output.Contains("0 selections; 0 unique"), output);
        });
    }
}
