using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static async Task WorkIqRenameGuidanceChecks(Func<string, Func<Task>, Task> check)
    {
        const string target = "/drives/drive/items/file";
        const string body = """{ "name": "editfile-renamed.txt" }""";
        foreach (string reference in new[] { "none", "sharepoint", "update-entity" })
            await check("Rename returned item evidence permits update then final without native readback " + reference, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                h.Handler.MutationResponse = new()
                {
                    ["statusCode"] = 200,
                    ["data"] = new JsonObject { ["id"] = "file", ["name"] = "editfile-renamed.txt" }
                };
                List<JsonObject> turns = [];
                if (reference != "none") turns.Add(LoadReference(reference, "guide"));
                turns.Add(Completion(tool: "update_entity", args: new { entityUrl = target, jsonBody = body }, id: "rename"));
                turns.Add(Completion("Renamed the requested item to editfile-renamed.txt, as returned by the successful update."));
                ModelHandler model = new(turns.ToArray());
                const string prompt = "Using AU WorkIQ, rename the already identified channel file to editfile-renamed.txt.";
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(prompt), default))!;
                Must(h.Handler.ToolCalls == 1 && h.Handler.Mutations == 1 &&
                    model.Requests.Count == turns.Count && output.Contains("Renamed the requested item"), output);
                string initial = SystemText(model.Requests[0]);
                Must(initial.Contains("For rename only:") && initial.Contains("sufficient evidence") &&
                    initial.Contains("explicit user-requested verification") && initial.Contains("pre-rename resolution/schema"));
                CheckLoaded(model.Requests[0]);
                if (reference != "none")
                {
                    CheckLoaded(model.Requests[1], reference);
                    string text = WorkIqSkill.ReferenceText(reference).Replace('\n', ' ');
                    Must(text.Contains("sufficient evidence") && text.Contains("ack-only") &&
                        text.Contains("distinct remaining task") && text.Contains("final model turn", StringComparison.OrdinalIgnoreCase));
                }
                JsonElement native = h.Handler.WireToolRequests.Single().GetProperty("params");
                Must(native.GetProperty("name").GetString() == "update_entity" &&
                    native.GetProperty("arguments").GetRawText() ==
                        JsonSerializer.SerializeToElement(new { entityUrl = target, jsonBody = body }).GetRawText());
                foreach (JsonElement request in model.Requests)
                    Must(request.GetProperty("messages")[2].GetProperty("content").GetString() == prompt);
                Must(model.Requests.Last().GetProperty("messages").EnumerateArray().Any(m =>
                    m.GetProperty("role").GetString() == "tool" &&
                    m.GetProperty("content").GetString()!.Contains("editfile-renamed.txt")));
            });

        foreach (string outcome in new[] { "empty", "ack-only", "pending", "ambiguous", "error" })
            await check("Rename incomplete or error response never gains scripted verification or replay " + outcome, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                h.Handler.MutationResponse = outcome switch
                {
                    "empty" => new(),
                    "ack-only" => new() { ["statusCode"] = 204 },
                    "pending" => new() { ["statusCode"] = 202, ["status"] = "pending" },
                    "ambiguous" => new() { ["statusCode"] = 200, ["data"] = new JsonObject { ["id"] = "different-item" } },
                    _ => new() { ["statusCode"] = 400, ["error"] = new JsonObject { ["code"] = "invalidRequest" } }
                };
                ModelHandler model = new(
                    Completion(tool: "update_entity", args: new { entityUrl = target, jsonBody = body }, id: "rename"),
                    Completion("Completion is not verified from the returned response; no repeat mutation was attempted."));
                string output = (await App(h, model, DirectOptions()).Handle(
                    Harness.Activity("Rename the established channel file; do not request extra verification."), default))!;
                Must(h.Handler.ToolCalls == 1 && h.Handler.Mutations == 1, output);
                if (outcome == "error") Must(model.Requests.Count == 1 && !output.Contains("Completion is not verified"), output);
                else Must(model.Requests.Count == 2 && output.Contains("Completion is not verified"), output);
            });

        foreach (bool verify in new[] { true, false })
            await check("Rename guidance retains explicit verification or distinct remaining read " + verify, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                h.Handler.MutationResponse = new()
                {
                    ["statusCode"] = 200, ["data"] = new JsonObject { ["id"] = "file", ["name"] = "editfile-renamed.txt" }
                };
                string read = verify ? target + "?$select=id,name" : "/drives/drive/items/dest";
                h.Handler.SectionTwoResults[read] = new() { ["id"] = verify ? "file" : "dest", ["name"] = "observed" };
                string prompt = verify ? "Rename the established file, then explicitly verify it with a fresh metadata read."
                    : "Rename the established file, then read the separate destination folder metadata.";
                ModelHandler model = new(
                    Completion(tool: "update_entity", args: new { entityUrl = target, jsonBody = body }, id: "rename"),
                    Completion(tool: "fetch", args: Fetch(read), id: "requested-read"), Completion("Returned requested observations."));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(prompt), default))!;
                Must(h.Handler.ToolCalls == 2 && h.Handler.Mutations == 1 && model.Requests.Count == 3 &&
                    output.Contains("Returned requested observations"), output);
                Must(h.Handler.WireToolRequests.Last().GetProperty("params").GetProperty("arguments").GetRawText() ==
                    JsonSerializer.SerializeToElement(Fetch(read)).GetRawText());
            });
    }
}
