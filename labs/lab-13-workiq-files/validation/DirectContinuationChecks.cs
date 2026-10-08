using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private const string PagePath = "/teams/team/channels/channel/messages";
    private static JsonElement PageArgs(params string[] urls) => JsonSerializer.SerializeToElement(new { entityUrls = urls });
    private static JsonObject Page(string? link, string key = "@odata.nextLink")
    {
        JsonObject page = new() { ["value"] = new JsonArray(new JsonObject { ["id"] = "message" }) };
        if (link is not null) page[key] = link;
        return page;
    }
    private static CallToolResult PageResult(JsonObject body) =>
        new() { Content = [], StructuredContent = JsonSerializer.SerializeToElement(body) };
    private static string DiagPart(string output) => output[output.IndexOf("\nContinuation diagnostic", StringComparison.Ordinal)..];

    private static async Task DirectContinuationChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (string reference in new[] { "none", "fetch", "teams" })
        foreach (bool returnedTop in new[] { false, true })
            await check("Channel initial no-top guidance preserves complete server link " + reference + " top=" + returnedTop, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                string initial = PagePath + "?$select=id,createdDateTime,from,subject,body,webUrl";
                string next = PagePath + "?$select=id%2ccreatedDateTime%2cfrom%2csubject%2cbody%2cwebUrl&$skiptoken=SYNTHETIC%2b+%2F";
                if (returnedTop) next += "&$top=7";
                h.Handler.SectionTwoResults[initial] = Page("https://graph.microsoft.com/v1.0" + next);
                h.Handler.SectionTwoResults[next] = Page(null);
                ModelHandler model = new();
                model.Respond = request =>
                {
                    int turn = model.Requests.Count;
                    if (reference != "none" && turn == 1) return LoadReference(reference, "guide");
                    if (turn == (reference == "none" ? 1 : 2))
                        return Completion(tool: "fetch", args: Fetch(initial), id: "initial");
                    if (turn == (reference == "none" ? 2 : 3))
                        return Completion(tool: DirectContinuationHandles.ToolName,
                            args: new { handle = ModelHandles(request).Single() }, id: "next");
                    return Completion("Observed root collection ended; separate replies and all Teams history are not established.");
                };
                string output = (await App(h, model, DirectOptions()).Handle(
                    Harness.Activity("Read invoking channel roots using WorkIQ only; page as needed."), default))!;
                string core = SystemText(model.Requests[0]).Replace('\n', ' ');
                Must(core.Contains("Initial channel-message fetch: omit `$top`") &&
                    core.Contains("`entityUrls` with relative strings") &&
                    core.Contains("including any server `$top`") && core.Contains("not all Teams history/replies"), core);
                CheckLoaded(model.Requests[0]);
                if (reference != "none")
                {
                    CheckLoaded(model.Requests[1], reference);
                    string loaded = WorkIqSkill.ReferenceText(reference).Replace('\n', ' ');
                    Must(loaded.Contains("initial request omitted it") && loaded.Contains("`%2c`") &&
                        loaded.Contains("fetch_next_page") && loaded.Contains("page size"), loaded);
                }
                Must(h.Handler.ToolCalls == 2 && h.Handler.Mutations == 0, output);
                Must(h.Handler.WireToolRequests[0].GetProperty("params").GetProperty("arguments").GetRawText() ==
                    JsonSerializer.SerializeToElement(Fetch(initial)).GetRawText());
                Must(h.Handler.WireToolRequests[1].GetProperty("params").GetProperty("arguments").GetRawText() ==
                    JsonSerializer.SerializeToElement(Fetch(next)).GetRawText());
                Must(output.Contains("knownOriginEquivalent=true") && output.Contains("rawTokenMatch=true") &&
                    output.Contains("$top:" + (returnedTop ? "same" : "absent")) &&
                    output.Contains("$select:same") && output.Contains("App continuation adapter selections: 1"), output);
            });

        foreach (string reference in new[] { "none", "fetch", "teams" })
        foreach (string variant in new[] { "preserved", "changed-select", "relative-form", "rejected-unchanged" })
            await check("Continuation guidance preserves server projection and native selected arguments " + reference + " " + variant, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                const string token = "SYNTHETIC-PRIVATE%2f%2B+cursor";
                string first = PagePath + "?$top=50&$select=id,body";
                string returned = PagePath + "?$select=body,id,createdDateTime&$skiptoken=" + token;
                string link = variant == "relative-form" ? "https://graph.microsoft.com/v1.0" + returned : returned;
                string outgoing = variant == "changed-select" ? returned.Replace("body,id,createdDateTime", "id,body", StringComparison.Ordinal) : returned;
                h.Handler.SectionTwoResults[first] = Page(link);
                h.Handler.SectionTwoResults[outgoing] = variant is "changed-select" or "rejected-unchanged"
                    ? new() { ["statusCode"] = 400, ["error"] = new JsonObject { ["code"] = "BadRequest" } } : Page(null);
                List<JsonObject> turns = [];
                if (reference != "none") turns.Add(LoadReference(reference, "guide"));
                turns.Add(Completion(tool: "fetch", args: Fetch(first), id: "initial"));
                turns.Add(Completion(tool: "fetch", args: Fetch(outgoing), id: "continuation"));
                turns.Add(Completion("Observed channel roots only; partial if the continuation was rejected. No filter fallback."));
                ModelHandler model = new(turns.ToArray());
                const string prompt = "Summarize this channel's messages using WorkIQ; report partial coverage honestly.";
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity(prompt), default))!;
                Must(h.Handler.ToolCalls == 2 && h.Handler.Mutations == 0 && model.Requests.Count == turns.Count, output);
                string core = SystemText(model.Requests[0]).Replace('\n', ' ');
                Must(core.Contains("fetch_next_page") && core.Contains("no URL rebuilding") &&
                    core.Contains("no filter fallback") && core.Contains("channels list roots"), core);
                CheckLoaded(model.Requests[0]);
                if (reference != "none")
                {
                    CheckLoaded(model.Requests[1], reference);
                    string loaded = WorkIqSkill.ReferenceText(reference).Replace('\n', ' ');
                    Must(loaded.Contains("character for character") && loaded.Contains("replyToId eq null") &&
                        loaded.Contains("relative", StringComparison.OrdinalIgnoreCase) &&
                        loaded.Contains("original") && loaded.Contains("projection"), loaded);
                }
                string receipt = DiagPart(output);
                Must(receipt.Contains("rawTokenMatch=true") && receipt.Contains("$select:" + (variant == "changed-select" ? "changed" : "same")) &&
                    receipt.Contains("exactUrlMatch=" + (variant is "changed-select" or "relative-form" ? "false" : "true")) &&
                    receipt.Contains("sourcePresentation=preserved-original-json-fragment-in-queued-model-message"), receipt);
                if (variant == "relative-form") Must(receipt.Contains("knownOriginEquivalent=true"), receipt);
                Must(!receipt.Contains(token) && !receipt.Contains(link), receipt);
                Must(h.Handler.WireToolRequests.Last().GetProperty("params").GetProperty("arguments").GetRawText() ==
                    JsonSerializer.SerializeToElement(Fetch(outgoing)).GetRawText());
                int afterPage = reference == "none" ? 1 : 2;
                Must(model.Requests[afterPage].GetProperty("messages").EnumerateArray().Any(m =>
                    m.GetProperty("role").GetString() == "tool" &&
                    m.GetProperty("content").GetString()!.Contains("body,id,createdDateTime")));
                foreach (JsonElement request in model.Requests)
                    Must(request.GetProperty("messages")[2].GetProperty("content").GetString() == prompt);
            });

        foreach (string variant in new[] { "exact", "token", "query", "absolute", "unicode", "resource400" })
            await check("Installed native continuation comparison preserves original dispatch and safe receipt " + variant, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                string first = PagePath + "?$top=50&$select=id,body";
                string token = variant == "unicode" ? "秘密😀%2F%2b+é" : "PRIVATE-OPAQUE%2F%2b+TOKEN";
                string relative = first + "&$skiptoken=" + token;
                string link = variant == "absolute" ? "https://graph.microsoft.com/v1.0" + relative : relative;
                string outgoing = variant switch
                {
                    "token" => relative.Replace("PRIVATE-OPAQUE", "CHANGED-OPAQUE", StringComparison.Ordinal),
                    "query" => relative.Replace("$top=50&$select=id,body", "$top=10&$select=id", StringComparison.Ordinal),
                    _ => relative
                };
                h.Handler.SectionTwoResults[first] = Page(link);
                h.Handler.SectionTwoResults[outgoing] = variant == "resource400"
                    ? new() { ["statusCode"] = 400, ["error"] = new JsonObject { ["code"] = "invalidRequest" } }
                    : Page(null);
                ModelHandler model = new(Completion(tool: "fetch", args: Fetch(first), id: "page-one"),
                    Completion(tool: "fetch", args: Fetch(outgoing), id: "page-two"),
                    Completion("Partial observed messages only."));
                string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Summarize channel messages."), default))!;
                Must(h.Handler.ToolCalls == 2 && h.Handler.Mutations == 0 && model.Requests.Count == 3, output);
                Must(h.Handler.WireToolRequests.Last().GetProperty("params").GetProperty("arguments").GetRawText() ==
                    JsonSerializer.SerializeToElement(Fetch(outgoing)).GetRawText());
                string diagnostic = DiagPart(output);
                Must(diagnostic.Contains("sourceCall=1") && diagnostic.Contains("outgoing call=2/entity=1") &&
                    diagnostic.Contains("sourceResultItem=1") && diagnostic.Contains("sourceLinkPresent=true") &&
                    diagnostic.Contains("rawTokenMatch=" + (variant == "token" ? "false" : "true")) &&
                    diagnostic.Contains("sourcePresentation=preserved-original-json-fragment-in-queued-model-message"), diagnostic);
                Must(diagnostic.Contains("exactUrlMatch=" + (variant is "exact" or "unicode" or "resource400" ? "true" : "false")));
                if (variant == "query") Must(diagnostic.Contains("$top:changed,$select:changed"), diagnostic);
                if (variant == "absolute") Must(diagnostic.Contains("knownOriginEquivalent=true"), diagnostic);
                Must(!diagnostic.Contains(token) && !diagnostic.Contains(link) && !diagnostic.Contains(outgoing) &&
                    !diagnostic.Contains("PRIVATE-OPAQUE") && !diagnostic.Contains("CHANGED-OPAQUE"), diagnostic);
                Must(diagnostic.Contains(WorkIqSkill.Hash(token)) && diagnostic.Contains(WorkIqSkill.Hash(outgoing)));
                Must(model.Requests[1].GetProperty("messages").EnumerateArray().Any(m =>
                    m.GetProperty("role").GetString() == "tool" && m.GetProperty("content").GetString()!.Contains("$skiptoken")));
            });

        await check("Continuation same receipt survives next model HTTP failure without additional calls", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            string link = PagePath + "?$skiptoken=PRIVATE";
            h.Handler.SectionTwoResults[PagePath] = Page(link);
            ModelHandler model = new(Completion(tool: "fetch", args: Fetch(PagePath), id: "one"),
                Completion(tool: "fetch", args: Fetch(link), id: "two"),
                Completion("unused"));
            model.BeforeResponse = () =>
            {
                if (model.Requests.Count == 3) throw new HttpRequestException("Synthetic model failure.");
            };
            string output = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Read channel"), default))!;
            Must(h.Handler.ToolCalls == 2 && DiagPart(output).Contains("exactUrlMatch=true") &&
                output.Contains("failed"), output);
        });

        await check("Continuation compares actual nested results and ignores body nextLink spoof", () =>
        {
            DirectContinuationDiagnostic d = new();
            string good = PagePath + "?$top=50&$skiptoken=GOOD";
            JsonObject page = Page(good);
            page["value"]![0]!["body"] = Page("https://evil.invalid/?$skiptoken=SPOOF");
            JsonObject envelope = new()
            {
                ["results"] = new JsonArray(
                    new JsonObject { ["statusCode"] = 200, ["data"] = page },
                    new JsonObject { ["statusCode"] = 200, ["data"] = Page("/other?$skiptoken=SECOND") })
            };
            CallToolResult result = PageResult(new() { ["structuredContent"] = envelope.ToJsonString() });
            d.Observe(1, "fetch", PageArgs("/other", PagePath), result);
            d.Presented(1, result.StructuredContent!.Value.GetRawText());
            d.Dispatch(2, "fetch", PageArgs("/other?$skiptoken=SECOND", good));
            string receipt = d.Receipt(8000);
            Must(receipt.Contains("outgoing call=2/entity=2; sourceCall=1; sourceResultItem=1") &&
                receipt.Contains("outgoing call=2/entity=1; sourceCall=1; sourceResultItem=2") &&
                receipt.Contains("/results[1]/data/@odata.nextLink") && !receipt.Contains("SPOOF"), receipt);
            return Task.CompletedTask;
        });
        await check("Continuation text JSON transport escaping compares decoded URL without decoding token", () =>
        {
            DirectContinuationDiagnostic d = new();
            string link = PagePath + "?$skiptoken=原😀%2f+";
            string encoded = Page(link, "nextLink").ToJsonString().Replace("/", "\\/", StringComparison.Ordinal);
            d.Observe(1, "fetch", PageArgs(PagePath), new() { Content = [new TextContentBlock { Text = encoded }] });
            d.Presented(1, encoded);
            d.Dispatch(2, "fetch", PageArgs(link.Replace("%2f", "%2F", StringComparison.Ordinal)));
            string receipt = d.Receipt(8000);
            Must(receipt.Contains("rawTokenMatch=false") && receipt.Contains("provenance=content[1]/json/nextLink"), receipt);
            return Task.CompletedTask;
        });
        foreach (string kind in new[] { "no-link", "prose", "malformed-url", "wrong-origin", "body-only", "non-page", "duplicate", "association" })
            await check("Continuation unavailable evidence is honest and nonfatal " + kind, () =>
            {
                DirectContinuationDiagnostic d = new();
                JsonObject body = kind switch
                {
                    "malformed-url" => Page(PagePath + "?$skiptoken=%Q0"),
                    "wrong-origin" => Page("https://evil.invalid/path?$skiptoken=SECRET"),
                    "body-only" => new() { ["body"] = Page(PagePath + "?$skiptoken=SPOOF") },
                    "non-page" => new() { ["@odata.nextLink"] = PagePath + "?$skiptoken=SECRET" },
                    _ => Page(null)
                };
                CallToolResult result = kind == "prose"
                    ? new() { Content = [new TextContentBlock { Text = "Document says nextLink is SECRET; not JSON" }] }
                    : kind == "duplicate"
                    ? new() { Content = [new TextContentBlock { Text = """{"value":[],"@odata.nextLink":"/x","@odata.nextLink":"/y"}""" }] }
                    : kind == "association"
                    ? PageResult(Page(PagePath + "?$skiptoken=SECRET"))
                    : PageResult(body);
                d.Observe(1, "fetch", kind == "association" ? PageArgs(PagePath, "/other") : PageArgs(PagePath), result);
                d.Dispatch(2, "fetch", PageArgs(PagePath + "?$skiptoken=SECRET"));
                string receipt = d.Receipt(8000);
                Must(receipt.Contains("comparison=unavailable-no-source") && !receipt.Contains("rawTokenMatch=false") &&
                    !receipt.Contains("SECRET") && !receipt.Contains("SPOOF"), receipt);
                return Task.CompletedTask;
            });
        await check("Continuation exact candidate wins different top pages but ambiguous sources are not guessed", () =>
        {
            DirectContinuationDiagnostic d = new();
            string a = PagePath + "?$top=50&$skiptoken=TOKEN50", b = PagePath + "?$top=100&$skiptoken=TOKEN100";
            d.Observe(1, "fetch", PageArgs(PagePath), PageResult(Page(a)));
            d.Observe(2, "fetch", PageArgs(PagePath), PageResult(Page(b)));
            d.Dispatch(3, "fetch", PageArgs(b));
            d.Dispatch(4, "fetch", PageArgs(PagePath + "?$top=10&$skiptoken=CHANGED"));
            string receipt = d.Receipt(8000);
            Must(receipt.Contains("outgoing call=3/entity=1; sourceCall=2") &&
                receipt.Contains("outgoing call=4/entity=1; sourceLinkPresent=true; comparison=ambiguous; candidateCount=2"), receipt);
            return Task.CompletedTask;
        });
        await check("Continuation presentation unavailable differs from preserved and receipt bounds are explicit", () =>
        {
            DirectContinuationDiagnostic d = new();
            string link = PagePath + "?$skiptoken=SECRET";
            d.Observe(1, "fetch", PageArgs(PagePath), PageResult(Page(link)));
            d.Dispatch(2, "fetch", PageArgs(link));
            Must(d.Receipt(8000).Contains("sourcePresentation=not-assessed"));
            d.Presented(1, "[withheld]");
            d.Dispatch(3, "fetch", PageArgs(link));
            Must(d.Receipt(8000).Contains("sourcePresentation=not-assessed-fragment-not-preserved"));
            for (int i = 4; i < 70; i++) d.Dispatch(i, "fetch", PageArgs(link));
            string receipt = d.Receipt(1400);
            Must(receipt.Length <= 1400 && receipt.Contains("comparisonRowLimit=true") &&
                !receipt.Contains("receiptRowsOmitted=0"), receipt);
            return Task.CompletedTask;
        });
        await check("Continuation run instances never share candidate state", () =>
        {
            DirectContinuationDiagnostic first = new(), second = new();
            string link = PagePath + "?$skiptoken=PRIVATE";
            first.Observe(1, "fetch", PageArgs(PagePath), PageResult(Page(link)));
            second.Dispatch(1, "fetch", PageArgs(link));
            Must(second.Receipt(8000).Contains("comparison=unavailable-no-source") &&
                !second.Receipt(8000).Contains("sourceCall=1"));
            return Task.CompletedTask;
        });
        await check("Installed concurrent invocations keep source hashes and comparisons isolated", async () =>
        {
            using Barrier bothRead = new(2);
            async Task<string> Run(string token)
            {
                await using Harness h = new(); DirectEnable(h);
                string link = PagePath + "?$skiptoken=" + token;
                h.Handler.SectionTwoResults[PagePath] = Page(link);
                h.Handler.SectionTwoResults[link] = Page(null);
                ModelHandler model = new(Completion(tool: "fetch", args: Fetch(PagePath), id: "one"),
                    Completion(tool: "fetch", args: Fetch(link), id: "two"), Completion("Bounded result."));
                model.BeforeResponse = () =>
                {
                    if (model.Requests.Count == 2) Must(bothRead.SignalAndWait(TimeSpan.FromSeconds(10)), "Concurrent fixture rendezvous timed out.");
                };
                Task<string?> running = App(h, model, DirectOptions()).Handle(Harness.Activity("Read channel"), default);
                string output = (await running)!;
                Must(h.Handler.ToolCalls == 2);
                return DiagPart(output);
            }
            string[] receipts = await Task.WhenAll(Task.Run(() => Run("JOB-A-TOKEN")), Task.Run(() => Run("JOB-B-TOKEN")));
            Must(receipts.All(r => r.Contains("exactUrlMatch=true")) &&
                receipts[0].Contains(WorkIqSkill.Hash("JOB-A-TOKEN")) &&
                !receipts[0].Contains(WorkIqSkill.Hash("JOB-B-TOKEN")) &&
                receipts[1].Contains(WorkIqSkill.Hash("JOB-B-TOKEN")) &&
                !receipts[1].Contains(WorkIqSkill.Hash("JOB-A-TOKEN")));
        });
        await check("Continuation JSON presentation loss is not claimed as source-token alteration", () =>
        {
            DirectContinuationDiagnostic d = new();
            string link = PagePath + "?$skiptoken=SECRET";
            CallToolResult result = PageResult(Page(link));
            d.Observe(1, "fetch", PageArgs(PagePath), result);
            d.Presented(1, result.StructuredContent!.Value.GetRawText()[..20]);
            d.Dispatch(2, "fetch", PageArgs(link));
            Must(d.Receipt(8000).Contains("sourcePresentation=not-assessed-fragment-not-preserved") &&
                d.Receipt(8000).Contains("rawTokenMatch=true"));
            return Task.CompletedTask;
        });
        await check("Private worker receives same per-job continuation receipt without wider scope", async () =>
        {
            string link = PagePath + "?$skiptoken=PRIVATE";
            await using PrivateHarness p = new(model: new(
                Completion(tool: "fetch", args: Fetch(PagePath), id: "one"),
                Completion(tool: "fetch", args: Fetch(link), id: "two"),
                Completion("Observed bounded pages.")));
            p.H.Handler.SectionTwoResults[PagePath] = Page(link);
            p.H.Handler.SectionTwoResults[link] = Page(null);
            PrivateJob job = await p.Initiate();
            await p.Authenticate(await p.Approve(job)); await p.Worker.RunOne(default);
            string output = p.Store.Get(job.Id).Result!;
            Must(p.H.Handler.ToolCalls == 2 && DiagPart(output).Contains("exactUrlMatch=true"), output);
        });
        await check("Legacy private continuation remains prohibited but returned page evidence survives", async () =>
        {
            const string path = "/drives/drive/items/root/children";
            string link = path + "?$skiptoken=PRIVATE";
            await using PrivateHarness p = new(model: new(
                Completion(tool: "fetch", args: Fetch(path), id: "one"),
                Completion(tool: "fetch", args: Fetch(link), id: "two")));
            p.H.Handler.SectionTwoResults[path] = Page(link);
            PrivateJob job = p.Legacy(await p.Initiate());
            await p.Authenticate(await p.Approve(job)); await p.Worker.RunOne(default);
            string output = p.Store.Get(job.Id).Result!;
            Must(p.H.Handler.ToolCalls == 1 && output.Contains("bounded select/top") &&
                DiagPart(output).Contains("sourceLinkPresent=true") &&
                !DiagPart(output).Contains("outgoing call=2"), output);
        });
    }
}
