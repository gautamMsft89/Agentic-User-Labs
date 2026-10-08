using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private const string ProvenanceChat = "19:PRIVATE-target-chat@unq.gbl.spaces";
    private const string ProvenanceDecoy = "19:PRIVATE-requester-chat@unq.gbl.spaces";
    private const string ProvenanceList = "/me/chats?$expand=members";
    private static JsonObject ProvenanceMember(string user) => new()
    {
        ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
        ["id"] = "PRIVATE-member-" + user, ["userId"] = user, ["displayName"] = "PRIVATE-label"
    };
    private static JsonObject ProvenanceEntity(string id, string counterpart, bool counted = true)
    {
        JsonObject chat = new()
        {
            ["id"] = id, ["chatType"] = "oneOnOne",
            ["members"] = new JsonArray(ProvenanceMember(Harness.Au), ProvenanceMember(counterpart)),
            ["body"] = new JsonObject { ["content"] = "PRIVATE unrelated message" }
        };
        if (counted) chat["members@odata.count"] = 2;
        return chat;
    }
    private static JsonObject ProvenanceEnvelope(string url, JsonObject data) =>
        new() { ["entityUrl"] = url, ["statusCode"] = 200, ["data"] = data };
    private static JsonElement ProvenanceWrite(string chat = ProvenanceChat) => JsonSerializer.SerializeToElement(new
    {
        parentUrl = "/chats/" + chat + "/messages",
        jsonBody = JsonSerializer.Serialize(new { body = new { contentType = "html", content = "Hi <at id=\"0\">PRIVATE label</at>" },
            mentions = new[] { new { id = 0, mentioned = new { user = new { id = Harness.Other } } } } })
    });
    private static string ChatDiagnostic(string output)
    {
        string diagnostic = output[output.IndexOf("\nChat provenance diagnostic", StringComparison.Ordinal)..];
        int privateError = diagnostic.IndexOf(DirectPrivateErrorReceipt.Marker, StringComparison.Ordinal);
        return privateError < 0 ? diagnostic : diagnostic[..privateError];
    }
    private static void SafeChatDiagnostic(string output)
    {
        Must(!output.Contains("PRIVATE") && !output.Contains(Harness.Au) && !output.Contains(Harness.Human) &&
            !output.Contains(Harness.Other) && !output.Contains("/me") && !output.Contains("/chats"), output);
        Must(output.Contains("targetIntent=not-assessed"), output);
    }
    private static async Task DirectChatProvenanceChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (string variant in new[] { "present", "absent", "decoy", "conflict", "unattributed", "expanded-uncounted" })
            await check("Installed chat provenance is safe on404 and preserves selected native payload " + variant, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                JsonObject target = ProvenanceEntity(ProvenanceChat, variant == "decoy" ? Harness.Human : Harness.Other,
                    variant != "expanded-uncounted");
                JsonArray values = new(ProvenanceEntity(ProvenanceDecoy, Harness.Human));
                if (variant != "absent") values.Add(target);
                if (variant == "conflict") values.Add(ProvenanceEntity(ProvenanceChat, Harness.Human));
                h.Handler.SectionTwoResource = url =>
                {
                    JsonObject data = url == "/me?$select=id" ? new() { ["id"] = Harness.Au } :
                        url == ProvenanceList ? new() { ["value"] = values.DeepClone() } :
                        new() { ["id"] = Harness.Other };
                    return variant == "unattributed" ? new() { ["statusCode"] = 200, ["data"] = data } : ProvenanceEnvelope(url, data);
                };
                h.Handler.MutationResponse = new() { ["statusCode"] = 404, ["error"] = new JsonObject { ["code"] = "notFound" } };
                var read = new { entityUrls = new[] { ProvenanceList, "/users/target@example.invalid", "/me?$select=id" } };
                JsonElement write = ProvenanceWrite();
                ModelHandler model = new(Completion(tool: "fetch", args: read, id: "lookup"),
                    Completion(tool: "create_entity", args: write, id: "send"));
                const string prompt = "Send one native person mention to the explicitly named person, not the invoking requester.";
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(prompt), default))!;
                Must(output.Contains("404") && output.Contains("no replay") && h.Handler.ToolCalls == 2 &&
                    h.Handler.Mutations == 1 && model.Requests.Count == 2, output);
                string diagnostic = ChatDiagnostic(output);
                SafeChatDiagnostic(diagnostic);
                Must(diagnostic.Contains("outgoing call=2") && diagnostic.Contains(WorkIqSkill.Hash(ProvenanceChat)), diagnostic);
                Must(diagnostic.Contains("selectedIdObserved=" + (variant == "absent" ? "not-observed-in-captured-eligible-data" :
                    variant == "unattributed" ? "unavailable" : "true")), diagnostic);
                if (variant == "present") Must(diagnostic.Contains("exactTwoPersonRoster=matches-caller-and-selected-mention") &&
                    diagnostic.Contains("presentation=preserved-in-queued-model-message"), diagnostic);
                if (variant == "decoy") Must(diagnostic.Contains("selectedMentionMemberObserved=false") &&
                    diagnostic.Contains("exactTwoPersonRoster=does-not-match"), diagnostic);
                if (variant == "conflict") Must(diagnostic.Contains("rosterEvidence=ambiguous-conflicting"), diagnostic);
                if (variant == "expanded-uncounted") Must(diagnostic.Contains("not-established-expansion") &&
                    diagnostic.Contains("exactTwoPersonRoster=not-established"), diagnostic);
                if (variant == "unattributed") Must(diagnostic.Contains("unattributed-result"), diagnostic);
                Must(h.Handler.WireToolRequests[0].GetProperty("params").GetProperty("arguments").GetRawText() ==
                    JsonSerializer.SerializeToElement(read).GetRawText());
                Must(h.Handler.WireToolRequests[1].GetProperty("params").GetProperty("arguments").GetRawText() == write.GetRawText());
                foreach (JsonElement request in model.Requests)
                {
                    Must(request.GetProperty("messages")[2].GetProperty("content").GetString() == prompt);
                    Must(!request.GetRawText().Contains("Chat provenance diagnostic"));
                }
            });

        await check("Chat provenance absent in completely attributed captured list is not fabricated verdict", () =>
        {
            DirectChatProvenanceDiagnostic d = new();
            d.Observe(1, "fetch", PageArgs(ProvenanceList), PageResult(new()
            {
                ["value"] = new JsonArray(ProvenanceEntity(ProvenanceDecoy, Harness.Human))
            }));
            d.Dispatch(2, "create_entity", ProvenanceWrite());
            string receipt = d.Receipt(5000);
            Must(receipt.Contains("selectedIdObserved=not-observed-in-captured-eligible-data") && !receipt.Contains("fabricated"), receipt);
            SafeChatDiagnostic(receipt);
            return Task.CompletedTask;
        });
        foreach (string kind in new[] { "plain", "partial", "missing-user", "unknown-type", "projected", "count-mismatch" })
            await check("Chat provenance plain versus partial typed roster " + kind, () =>
            {
                DirectChatProvenanceDiagnostic d = new();
                d.Observe(1, "fetch", PageArgs("/me"), PageResult(new() { ["id"] = Harness.Au }));
                JsonObject chat = new() { ["id"] = ProvenanceChat, ["chatType"] = "oneOnOne" };
                d.Observe(2, "fetch", PageArgs("/chats/" + ProvenanceChat), PageResult(chat));
                JsonObject target = ProvenanceMember(Harness.Other);
                if (kind == "missing-user") target.Remove("userId");
                if (kind == "unknown-type") target["@odata.type"] = "#microsoft.graph.conversationMember";
                JsonObject roster = new() { ["value"] = new JsonArray(ProvenanceMember(Harness.Au), target) };
                if (kind == "partial") roster["@odata.nextLink"] = "/PRIVATE-next";
                if (kind == "count-mismatch") roster["@odata.count"] = 3;
                string url = "/chats/" + ProvenanceChat + "/members" + (kind == "projected" ? "?$select=id" : "");
                d.Observe(3, "fetch", PageArgs(url), PageResult(roster));
                d.Presented(3, roster.ToJsonString()); // Exact JSON encoding is not presumed to match the original fragment.
                d.Dispatch(4, "create_entity", ProvenanceWrite());
                string receipt = d.Receipt(5000);
                Must(receipt.Contains("exactTwoPersonRoster=" + (kind == "plain" ? "matches-caller-and-selected-mention" : "not-established")), receipt);
                SafeChatDiagnostic(receipt);
                return Task.CompletedTask;
            });
        foreach (string kind in new[] { "body", "schema", "malformed", "duplicate", "wrong-url", "conflicting-url", "partial-wrapper", "mcp-error" })
            await check("Chat provenance rejects spoofed or unavailable source " + kind, () =>
            {
                DirectChatProvenanceDiagnostic d = new();
                JsonObject data = new() { ["value"] = new JsonArray(ProvenanceEntity(ProvenanceChat, Harness.Other)) };
                JsonObject envelope = ProvenanceEnvelope(ProvenanceList, data);
                string tool = kind == "schema" ? "get_schema" : "fetch";
                if (kind == "body") envelope["data"] = new JsonObject { ["body"] = data.DeepClone() };
                if (kind == "wrong-url") envelope["entityUrl"] = "/chats/unrequested";
                if (kind == "conflicting-url") envelope["url"] = "/me";
                if (kind == "partial-wrapper") envelope["truncated"] = true;
                CallToolResult result = kind == "malformed" ? new() { Content = [new TextContentBlock { Text = "{invalid" }] } :
                    kind == "duplicate" ? new() { Content = [new TextContentBlock { Text = """{"id":"a","id":"b"}""" }] } : PageResult(envelope);
                if (kind == "mcp-error") result.IsError = true;
                d.Observe(1, tool, PageArgs(ProvenanceList), result);
                d.Dispatch(2, "create_entity", ProvenanceWrite());
                string receipt = d.Receipt(5000);
                Must(!receipt.Contains("selectedIdObserved=true") && receipt.Contains("exactTwoPersonRoster=not-established"), receipt);
                SafeChatDiagnostic(receipt);
                return Task.CompletedTask;
            });
        await check("Chat provenance reversed echoed batch and mirrored payload keep actual source provenance", () =>
        {
            DirectChatProvenanceDiagnostic d = new();
            JsonObject body = new()
            {
                ["results"] = new JsonArray(
                    ProvenanceEnvelope(ProvenanceList, new() { ["value"] = new JsonArray(ProvenanceEntity(ProvenanceChat, Harness.Other)) }),
                    ProvenanceEnvelope("/me", new() { ["id"] = Harness.Au }))
            };
            CallToolResult result = PageResult(body);
            result.Content = [new TextContentBlock { Text = result.StructuredContent!.Value.GetRawText() }];
            d.Observe(1, "fetch", PageArgs("/me", ProvenanceList), result);
            d.Presented(1, result.StructuredContent.Value.GetRawText());
            d.Dispatch(2, "create_entity", ProvenanceWrite(Uri.EscapeDataString(ProvenanceChat)));
            string receipt = d.Receipt(5000);
            Must(receipt.Contains("matchingChatObservations=1") && receipt.Contains("source call=1/resultItem=1; kind=chat") &&
                receipt.Contains("source call=1/resultItem=2; kind=self") &&
                receipt.Contains("exactTwoPersonRoster=matches-caller-and-selected-mention"), receipt);
            SafeChatDiagnostic(receipt);
            return Task.CompletedTask;
        });
        await check("Chat provenance unqueued and changed presentation stay unassessed", () =>
        {
            DirectChatProvenanceDiagnostic d = new();
            CallToolResult result = PageResult(ProvenanceEntity(ProvenanceChat, Harness.Other));
            d.Observe(1, "fetch", PageArgs("/chats/" + ProvenanceChat), result);
            d.Dispatch(2, "create_entity", ProvenanceWrite());
            Must(d.Receipt(5000).Contains("presentation=not-assessed"));
            d.Presented(1, "[withheld]");
            d.Dispatch(3, "create_entity", ProvenanceWrite());
            Must(d.Receipt(5000).Contains("not-assessed-fragment-unavailable"));
            return Task.CompletedTask;
        });
        await check("Chat provenance source member and receipt bounds disclose omitted evidence", () =>
        {
            DirectChatProvenanceDiagnostic d = new();
            JsonArray values = [];
            for (int i = 0; i < 70; i++) values.Add(ProvenanceEntity("chat-" + i, Harness.Other));
            d.Observe(1, "fetch", PageArgs(ProvenanceList), PageResult(new() { ["value"] = values }));
            d.Dispatch(2, "create_entity", ProvenanceWrite());
            string receipt = d.Receipt(1000);
            Must(receipt.Length <= 1000 && receipt.Contains("sourceLimit=True") && receipt.Contains("dispatchRowsOmitted="), receipt);
            DirectChatProvenanceDiagnostic members = new();
            JsonArray roster = [];
            for (int i = 0; i < 129; i++) roster.Add(ProvenanceMember("user-" + i));
            members.Observe(1, "fetch", PageArgs("/chats/" + ProvenanceChat + "/members"), PageResult(new() { ["value"] = roster }));
            members.Dispatch(2, "create_entity", ProvenanceWrite());
            Must(members.Receipt(5000).Contains("member-limit"));
            return Task.CompletedTask;
        });
        await check("Chat provenance separate concurrent jobs never share source state", async () =>
        {
            async Task<string> Run(bool present)
            {
                DirectChatProvenanceDiagnostic d = new();
                await Task.Yield();
                if (present) d.Observe(1, "fetch", PageArgs("/chats/" + ProvenanceChat), PageResult(ProvenanceEntity(ProvenanceChat, Harness.Other)));
                await Task.Yield();
                d.Dispatch(2, "create_entity", ProvenanceWrite());
                return d.Receipt(5000);
            }
            string[] results = await Task.WhenAll(Run(true), Run(false));
            Must(results[0].Contains("selectedIdObserved=true") &&
                results[1].Contains("selectedIdObserved=not-observed-in-captured-eligible-data"));
        });
        await check("Private Direct404 receipt includes chat provenance without approval or dispatch expansion", async () =>
        {
            ModelHandler model = new(Completion(tool: "fetch", args: Fetch("/chats/" + ProvenanceChat), id: "lookup"),
                Completion(tool: "create_entity", args: ProvenanceWrite(), id: "send"));
            await using PrivateHarness p = new(model);
            p.H.Handler.SectionTwoResults["/chats/" + ProvenanceChat] = ProvenanceEntity(ProvenanceChat, Harness.Other);
            p.H.Handler.MutationResponse = new() { ["statusCode"] = 404, ["error"] = new JsonObject { ["code"] = "notFound" } };
            PrivateJob job = await p.Initiate();
            await p.Authenticate(await p.Approve(job)); await p.Worker.RunOne(default);
            Must(p.H.Handler.ToolCalls == 2 && p.H.Handler.Mutations == 1 && model.Requests.Count == 2);
            string result = p.Store.Get(job.Id).Result!;
            Must(result.Contains("404") && result.Contains("Chat provenance diagnostic"), result);
            SafeChatDiagnostic(ChatDiagnostic(result));
            Must(p.H.Handler.WireToolRequests.Last().GetProperty("params").GetProperty("arguments").GetRawText() == ProvenanceWrite().GetRawText());
        });
    }
}
