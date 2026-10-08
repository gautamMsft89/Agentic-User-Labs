using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static async Task WorkIqMoveGuidanceChecks(Func<string, Func<Task>, Task> check)
    {
        const string source = "/drives/channel-drive/items/source-file";
        const string children = "/drives/channel-drive/items/test-folder/children";
        const string original = "Using the AU and WorkIQ, move editfile-renamed.txt into existing test in this channel. Preserve name/content; stop if missing or collision.";
        foreach (string reference in new[] { "sharepoint", "update-entity", "do-action" })
            await check("Move contract distinction is exposed in initial core and selected reference " + reference, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                ModelHandler model = new(LoadReference(reference, "guide"), Completion("No operation performed."));
                await App(h, model, DirectOptions()).Handle(Harness.Activity(original), default);
                string system = SystemText(model.Requests[0]);
                Must(system.Contains("DriveItem same-drive move is an item update, NOT a `/move` POST action") &&
                    system.Contains("missing-folder/collision checks") && system.Contains("no fabricated do_action fallback or copy+delete"));
                CheckLoaded(model.Requests[0]); CheckLoaded(model.Requests[1], reference);
                string text = WorkIqSkill.ReferenceText(reference).Replace('\n', ' ');
                Must(text.Contains("parentReference") && text.Contains("update_entity") &&
                    text.Contains("/move") && text.Contains("copy+delete"));
                Must(WorkIqSkill.References.Single(r => r.Id == "update-entity").Description.Contains("NOT /move action"));
                Must(h.Handler.ToolCalls == 0);
            });

        foreach (bool requireDriveId in new[] { false, true })
            await check("Model-selected same-drive move preserves raw update arguments and preflight " + requireDriveId, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                string folder = Channel + "/filesFolder";
                const string root = "/drives/channel-drive/items/channel-root/children";
                h.Handler.SectionTwoResults[folder] = new()
                {
                    ["id"] = "channel-root", ["parentReference"] = new JsonObject { ["driveId"] = "channel-drive" }
                };
                h.Handler.SectionTwoResults[root] = new()
                {
                    ["value"] = new JsonArray(
                        new JsonObject { ["id"] = "source-file", ["name"] = "editfile-renamed.txt", ["file"] = new JsonObject() },
                        new JsonObject { ["id"] = "test-folder", ["name"] = "test", ["folder"] = new JsonObject() })
                };
                h.Handler.SectionTwoResults[children] = new() { ["value"] = new JsonArray() };
                string schema = requireDriveId
                    ? "update = { parentReference: { id: tstr, driveId: tstr } }"
                    : "update = { parentReference: { id: tstr } }";
                h.Handler.DiscoveryMcpResult = new()
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = schema })
                };
                h.Handler.MutationResponse = new()
                {
                    ["statusCode"] = 200, ["data"] = new JsonObject
                    {
                        ["id"] = "source-file", ["name"] = "editfile-renamed.txt",
                        ["parentReference"] = new JsonObject { ["id"] = "test-folder", ["driveId"] = "channel-drive" }
                    }
                };
                string body = requireDriveId
                    ? """{ "parentReference": { "id":"test-folder", "driveId":"channel-drive" } }"""
                    : """{ "parentReference": { "id":"test-folder" } }""";
                ModelHandler model = new(LoadReference("sharepoint", "site-guide"),
                    Completion(tool: "fetch", args: Fetch(folder), id: "folder"),
                    Completion(tool: "fetch", args: Fetch(root), id: "root"),
                    Completion(tool: "fetch", args: Fetch(children), id: "collision"),
                    LoadReference("update-entity", "move-guide"),
                    Completion(tool: "get_schema", args: new { path = source, operationType = "update", format = "cddl" }, id: "schema"),
                    Completion(tool: "update_entity", args: new { entityUrl = source, jsonBody = body }, id: "move"),
                    Completion("The native response places the same item in test with its basename unchanged."));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(original), default))!;
                Must(h.Handler.ToolCalls == 5 && h.Handler.Mutations == 1 && model.Requests.Count == 8 &&
                    output.Contains("basename unchanged"), output);
                Must(h.Handler.Calls.Select(c => c.Tool).SequenceEqual(["fetch", "fetch", "fetch", "get_schema", "update_entity"]));
                JsonElement call = h.Handler.WireToolRequests.Last().GetProperty("params");
                Must(call.GetProperty("arguments").GetRawText() ==
                    JsonSerializer.SerializeToElement(new { entityUrl = source, jsonBody = body }).GetRawText());
                Must(model.Requests[6].GetProperty("messages").EnumerateArray().Any(m =>
                    m.GetProperty("role").GetString() == "tool" && m.GetProperty("content").GetString()!.Contains(schema)));
                foreach (JsonElement request in model.Requests)
                    Must(request.GetProperty("messages")[2].GetProperty("content").GetString() == original);
                CheckLoaded(model.Requests[7], "sharepoint", "update-entity");
            });

        await check("Unsupported native move schema remains model-selected honest stop with no action fallback", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.DiscoveryMcpResult = new()
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "update = { name: tstr } ; parentReference unsupported" })
            };
            ModelHandler model = new(LoadReference("update-entity", "guide"),
                Completion(tool: "get_schema", args: new { path = source, operationType = "update", format = "cddl" }, id: "schema"),
                Completion("The returned update contract does not support parentReference; no move was attempted."));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(original), default))!;
            Must(h.Handler.ToolCalls == 1 && h.Handler.Mutations == 0 && output.Contains("no move was attempted"), output);
        });

        foreach (string issue in new[] { "missing", "collision", "incomplete" })
            await check("Move requested precondition stops before mutation " + issue, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                JsonArray values = issue == "collision"
                    ? new JsonArray(new JsonObject { ["id"] = "different-file", ["name"] = "editfile-renamed.txt" }) : new();
                JsonObject result = new() { ["value"] = values };
                if (issue == "incomplete") result["@odata.nextLink"] = "https://synthetic.invalid/continuation";
                string preflight = issue == "missing" ? "/drives/channel-drive/items/channel-root/children" : children;
                h.Handler.SectionTwoResults[preflight] = result;
                ModelHandler model = new(LoadReference("update-entity", "guide"),
                    Completion(tool: "fetch", args: Fetch(preflight), id: "preflight"),
                    Completion("The requested precondition is not established (" + issue + "); no move attempted."));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(original), default))!;
                Must(h.Handler.ToolCalls == 1 && h.Handler.Mutations == 0 && output.Contains("no move attempted"), output);
            });

        await check("Rejected native move update stops without invented action or copy delete fallback", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            h.Handler.MutationResponse = new()
            {
                ["statusCode"] = 400, ["error"] = new JsonObject { ["code"] = "invalidRequest" }
            };
            ModelHandler model = new(Completion(tool: "update_entity",
                args: new { entityUrl = source, jsonBody = """{"parentReference":{"id":"test-folder"}}""" }, id: "move"),
                Completion(tool: "do_action", args: new { actionUrl = source + "/move", jsonBody = "{}" }, id: "must-not-run"));
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(original), default))!;
            Must(h.Handler.ToolCalls == 1 && h.Handler.Mutations == 1 && model.Requests.Count == 1 &&
                !h.Handler.Calls.Any(c => c.Tool == "do_action"), output);
        });
    }
}
