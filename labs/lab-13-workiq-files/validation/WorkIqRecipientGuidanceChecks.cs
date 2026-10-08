using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static async Task WorkIqRecipientGuidanceChecks(Func<string, Func<Task>, Task> check)
    {
        const string personPath = "/users/target%40example.invalid?$select=id,displayName,mail,userPrincipalName";
        const string selfPath = "/me?$select=id";
        const string chatsPath = "/me/chats?$expand=members";
        const string correctChat = "19:opaque-target-chat@unq.gbl.spaces";
        const string decoyChat = "19:opaque-requester-chat@unq.gbl.spaces";
        const string prompt = "Using the configured AU and WorkIQ only, send a one-to-one message to target@example.invalid saying Hi, please review the onboarding checklist. Make the recipient a native Teams person mention. Stop without a substitute if the recipient is ambiguous or native mentions are unsupported.";
        var reads = new { entityUrls = new[] { selfPath, personPath, chatsPath } };

        JsonObject Member(string id, string user) => new()
        {
            ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
            ["id"] = id, ["userId"] = user, ["displayName"] = "Example Person",
            ["email"] = "same-label@example.invalid", ["tenantId"] = Harness.Tenant
        };
        JsonObject Chat(string id, string counterpart) => new()
        {
            ["id"] = id, ["chatType"] = "oneOnOne",
            ["members"] = new JsonArray(Member("opaque-caller-member", Harness.Au), Member("opaque-counterpart-member", counterpart))
        };
        void Seed(Harness h, JsonObject chats)
        {
            DirectEnable(h);
            h.Handler.SectionTwoResults[selfPath] = new() { ["id"] = Harness.Au };
            h.Handler.SectionTwoResults[personPath] = new()
            {
                ["id"] = Harness.Other, ["displayName"] = "Example Person",
                ["mail"] = "target@example.invalid", ["userPrincipalName"] = "target@example.invalid"
            };
            h.Handler.SectionTwoResults[chatsPath] = chats;
        }

        // These scripted model choices test guidance delivery and wire fidelity, not model reasoning.
        foreach (bool reversed in new[] { false, true })
            await check("Recipient roster guidance reaches installed model and exact selected chat wire " + reversed, async () =>
            {
                await using Harness h = new();
                JsonObject decoy = Chat(decoyChat, Harness.Human), correct = Chat(correctChat, Harness.Other);
                Seed(h, new() { ["value"] = reversed ? new JsonArray(correct, decoy) : new JsonArray(decoy, correct) });
                string body = JsonSerializer.Serialize(new
                {
                    body = new { contentType = "html", content = "Hi <at id=\"0\">Example Person</at>, please review the onboarding checklist." },
                    mentions = new[] { new { id = 0, mentionText = "Example Person",
                        mentioned = new { user = new { id = Harness.Other, displayName = "Example Person", userIdentityType = "aadUser" } } } }
                });
                var write = new { parentUrl = "/chats/" + correctChat + "/messages", jsonBody = body };
                h.Handler.MutationResponse = new()
                {
                    ["statusCode"] = 201, ["data"] = new JsonObject { ["id"] = "accepted-message", ["chatId"] = correctChat }
                };
                ModelHandler model = new(LoadReference("teams", "teams"),
                    Completion(tool: "fetch", args: reads, id: "resolve"),
                    LoadReference("create-entity", "create-guide"),
                    Completion(tool: "create_entity", args: write, id: "send"),
                    Completion("WorkIQ accepted the message in the returned oneOnOne chat whose roster contains the configured caller and target@example.invalid. No notification or read status is established."));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(prompt), default))!;
                Must(h.Handler.ToolCalls == 2 && h.Handler.Mutations == 1 && model.Requests.Count == 5 &&
                    output.Contains("WorkIQ accepted") && output.Contains("No notification or read status"), output);
                string initial = SystemText(model.Requests[0]).Replace('\n', ' ');
                Must(initial.Contains("NOT a default destination") &&
                    initial.Contains("complete typed member userIds") && initial.Contains("NOT delivery") &&
                    initial.Contains("Never claim delivery to a person based only on mention") &&
                    initial.Contains("do NOT request `$select=userId,email`") &&
                    initial.Contains("NO query string") && initial.Contains("stop without sending or trying query variations"));
                CheckLoaded(model.Requests[0]); CheckLoaded(model.Requests[1], "teams");
                CheckLoaded(model.Requests[3], "teams", "create-entity");
                string teamGuide = WorkIqSkill.ReferenceText("teams").Replace('\n', ' ');
                Must(teamGuide.Contains("Existing-chat selection from returned rosters") &&
                    teamGuide.Contains("compare `userId`") && teamGuide.Contains("human requester is not a substitute") &&
                    teamGuide.Contains("`chatType=\"oneOnOne\"`") && teamGuide.Contains("A channel post cannot substitute"));
                Must(WorkIqSkill.ReferenceText("create-entity").Contains("does not select the delivery conversation"));
                JsonElement nativeRead = h.Handler.WireToolRequests[0].GetProperty("params");
                JsonElement nativeWrite = h.Handler.WireToolRequests[1].GetProperty("params");
                Must(nativeRead.GetProperty("name").GetString() == "fetch" &&
                    nativeRead.GetProperty("arguments").GetRawText() == JsonSerializer.SerializeToElement(reads).GetRawText() &&
                    nativeWrite.GetProperty("name").GetString() == "create_entity" &&
                    nativeWrite.GetProperty("arguments").GetRawText() == JsonSerializer.SerializeToElement(write).GetRawText());
                string returned = string.Join("\n", model.Requests[2].GetProperty("messages").EnumerateArray()
                    .Where(m => m.GetProperty("role").GetString() == "tool").Select(m => m.GetProperty("content").GetString()));
                Must(returned.Contains(decoyChat) && returned.Contains(correctChat) && returned.Contains(Harness.Au) &&
                    returned.Contains(Harness.Other) && returned.Contains(Harness.Human) && returned.Contains("opaque-counterpart-member"));
                Must(model.Requests.Last().GetProperty("messages").EnumerateArray().Any(m =>
                    m.GetProperty("role").GetString() == "tool" && m.GetProperty("content").GetString()!.Contains("accepted-message")));
                foreach (JsonElement request in model.Requests)
                    Must(request.GetProperty("messages")[2].GetProperty("content").GetString() == prompt);
            });

        foreach (string suffix in new[]
        {
            "?$select=id,chatType&$expand=members($select=id,displayName,userId,email)",
            "/members?$select=id,displayName,userId,email"
        })
            await check("Unsupported roster projection is not repaired or retried on native wire " + suffix, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                string path = "/chats/" + decoyChat + suffix;
                h.Handler.SectionTwoResource = _ => new JsonObject
                {
                    ["statusCode"] = 400, ["error"] = new JsonObject
                    {
                        ["code"] = "BadRequest",
                        ["message"] = "Could not find a property named 'userId' on type 'microsoft.graph.conversationMember'."
                    }
                };
                var args = new { entityUrls = new[] { path } };
                ModelHandler model = new(Completion(tool: "fetch", args: args, id: "rejected-roster"),
                    Completion("Roster request failed; no message was sent and no query variation was attempted."));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(prompt), default))!;
                // Existing conservative effect classification stops the expanded chat form at its error.
                bool stoppedAtError = suffix.StartsWith("?", StringComparison.Ordinal);
                Must(h.Handler.ToolCalls == 1 && h.Handler.Mutations == 0 &&
                    model.Requests.Count == (stoppedAtError ? 1 : 2) &&
                    output.Contains(stoppedAtError ? "Workflow stopped, no replay" : "no message was sent"), output);
                Must(output.Contains("unsupported-property-reported"), output);
                Must(h.Handler.WireToolRequests.Single().GetProperty("params").GetProperty("arguments").GetRawText() ==
                    JsonSerializer.SerializeToElement(args).GetRawText());
                CheckLoaded(model.Requests[0]);
            });

        foreach (string condition in new[] { "missing", "partial", "extra-member", "group", "duplicate-candidates", "requester-only", "ambiguous-person" })
            await check("Scripted unresolved recipient roster stops without native send " + condition, async () =>
            {
                await using Harness h = new();
                JsonObject candidate = Chat(correctChat, Harness.Other);
                JsonArray values = new(candidate);
                switch (condition)
                {
                    case "missing": candidate.Remove("members"); break;
                    case "partial": candidate["members@odata.nextLink"] = "/chats/opaque/members?$skiptoken=synthetic"; break;
                    case "extra-member": candidate["members"]!.AsArray().Add(Member("extra", Harness.Human)); break;
                    case "group": candidate["chatType"] = "group"; break;
                    case "duplicate-candidates": values.Add(Chat("19:second-target-chat", Harness.Other)); break;
                    case "requester-only": values = new JsonArray(Chat(decoyChat, Harness.Human)); break;
                }
                Seed(h, new() { ["value"] = values });
                if (condition == "ambiguous-person")
                    h.Handler.SectionTwoResults[personPath] = new() { ["error"] = new JsonObject { ["code"] = "ambiguousIdentity" } };
                ModelHandler model = new(LoadReference("teams", "teams"),
                    Completion(tool: "fetch", args: reads, id: "resolve"),
                    Completion("The requested recipient/destination is not established from this evidence. No message was sent; no current-chat or channel substitute was used."));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(prompt), default))!;
                Must(h.Handler.ToolCalls == 1 && h.Handler.Mutations == 0 && model.Requests.Count == 3 &&
                    output.Contains("No message was sent"), output);
                CheckLoaded(model.Requests.Last(), "teams");
                Must(h.Handler.WireToolRequests.Single().GetProperty("params").GetProperty("arguments").GetRawText() ==
                    JsonSerializer.SerializeToElement(reads).GetRawText());
            });

        foreach (string reference in new[] { "none", "teams", "fetch" })
        foreach (string outcome in new[] { "complete", "missing-userId", "base-type-only", "unsupported-property" })
            await check("Nonprojected typed roster installed wire and scripted no-send boundaries " + reference + " " + outcome, async () =>
            {
                await using Harness h = new();
                Seed(h, new());
                const string list = "/me/chats";
                string decoyMembers = "/chats/" + decoyChat + "/members";
                string targetMembers = "/chats/" + correctChat + "/members";
                h.Handler.SectionTwoResults[list] = new()
                {
                    ["value"] = new JsonArray(
                        new JsonObject { ["id"] = decoyChat, ["chatType"] = "oneOnOne" },
                        new JsonObject { ["id"] = correctChat, ["chatType"] = "oneOnOne" })
                };
                JsonObject target = Member("opaque-target-member", Harness.Other);
                if (outcome == "missing-userId") target.Remove("userId");
                if (outcome == "base-type-only")
                {
                    target["@odata.type"] = "#microsoft.graph.conversationMember";
                    target.Remove("userId"); target.Remove("email"); target.Remove("tenantId");
                }
                h.Handler.SectionTwoResults[decoyMembers] = new()
                {
                    ["value"] = new JsonArray(Member("opaque-self", Harness.Au), Member("opaque-requester", Harness.Human))
                };
                h.Handler.SectionTwoResults[targetMembers] = new()
                {
                    ["value"] = new JsonArray(Member("opaque-self", Harness.Au), target)
                };
                if (outcome == "unsupported-property")
                    h.Handler.SectionTwoResource = path => path == targetMembers ? new JsonObject
                    {
                        ["statusCode"] = 400,
                        ["error"] = new JsonObject { ["code"] = "BadRequest",
                            ["message"] = "Could not find a property named 'userId' on type 'microsoft.graph.conversationMember'." }
                    } : null;
                var resolve = new { entityUrls = new[] { selfPath, personPath, list } };
                var roster = new { entityUrls = new[] { decoyMembers, targetMembers } };
                string body = JsonSerializer.Serialize(new
                {
                    body = new { contentType = "html", content = "Hi <at id=\"0\">Example Person</at>, please review the onboarding checklist." },
                    mentions = new[] { new { id = 0, mentionText = "Example Person",
                        mentioned = new { user = new { id = Harness.Other, displayName = "Example Person", userIdentityType = "aadUser" } } } }
                });
                var write = new { parentUrl = "/chats/" + correctChat + "/messages", jsonBody = body };
                h.Handler.MutationResponse = new()
                {
                    ["statusCode"] = 201, ["data"] = new JsonObject { ["id"] = "typed-roster-message", ["chatId"] = correctChat }
                };
                List<JsonObject> turns = [];
                if (reference != "none") turns.Add(LoadReference(reference, "guide"));
                turns.Add(Completion(tool: "fetch", args: resolve, id: "resolve"));
                turns.Add(Completion(tool: "fetch", args: roster, id: "rosters"));
                if (outcome == "complete") turns.Add(Completion(tool: "create_entity", args: write, id: "send"));
                turns.Add(Completion(outcome == "complete"
                    ? "WorkIQ accepted the message in the oneOnOne chat with the resolved target; the requester chat was not the destination."
                    : "Recipient roster is unresolved from the returned evidence. No message was sent; no query variations or substitute destination were attempted."));
                ModelHandler model = new(turns.ToArray());
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(prompt), default))!;
                Must(model.Requests.Count == turns.Count &&
                    h.Handler.ToolCalls == (outcome == "complete" ? 3 : 2) &&
                    h.Handler.Mutations == (outcome == "complete" ? 1 : 0), output);
                Must(output.Contains(outcome == "complete" ? "WorkIQ accepted" : "No message was sent"), output);
                string initial = SystemText(model.Requests[0]).Replace('\n', ' ');
                Must(initial.Contains("do NOT request `$select=userId,email`") &&
                    initial.Contains("base conversationMember") && initial.Contains("NO query string") &&
                    initial.Contains("Returned fields do not establish selectable properties"));
                CheckLoaded(model.Requests[0]);
                if (reference != "none")
                {
                    CheckLoaded(model.Requests[1], reference);
                    string guidance = WorkIqSkill.ReferenceText(reference).Replace('\n', ' ').Replace("`", "");
                    Must(guidance.Contains("NO query string") && guidance.Contains("no OData") &&
                        guidance.Contains("unsupported-property") && guidance.Contains("actual userId"));
                }
                JsonElement resolveWire = h.Handler.WireToolRequests[0].GetProperty("params");
                JsonElement rosterWire = h.Handler.WireToolRequests[1].GetProperty("params");
                Must(resolveWire.GetProperty("arguments").GetRawText() == JsonSerializer.SerializeToElement(resolve).GetRawText() &&
                    rosterWire.GetProperty("arguments").GetRawText() == JsonSerializer.SerializeToElement(roster).GetRawText());
                Must(rosterWire.GetProperty("arguments").GetProperty("entityUrls").EnumerateArray()
                    .All(url => url.GetString()!.EndsWith("/members", StringComparison.Ordinal) && !url.GetString()!.Contains('?')));
                if (outcome == "complete")
                    Must(h.Handler.WireToolRequests[2].GetProperty("params").GetProperty("arguments").GetRawText() ==
                        JsonSerializer.SerializeToElement(write).GetRawText());
                int afterRoster = reference == "none" ? 2 : 3;
                string results = string.Join("\n", model.Requests[afterRoster].GetProperty("messages").EnumerateArray()
                    .Where(m => m.GetProperty("role").GetString() == "tool").Select(m => m.GetProperty("content").GetString()));
                Must(results.Contains(Harness.Human) && results.Contains("opaque-requester"));
                if (outcome == "complete")
                    Must(results.Contains("aadUserConversationMember") && results.Contains("userId") &&
                        results.Contains("email") && results.Contains(Harness.Other) && results.Contains("opaque-target-member"));
                if (outcome == "unsupported-property") Must(results.Contains("400"));
                foreach (JsonElement request in model.Requests)
                    Must(request.GetProperty("messages")[2].GetProperty("content").GetString() == prompt);
            });
    }
}
