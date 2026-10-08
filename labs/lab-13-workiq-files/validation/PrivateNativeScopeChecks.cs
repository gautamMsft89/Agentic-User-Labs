using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static async Task PrivateNativeScopeChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (string query in new[] { "sample.txt", "O''Brien%20report.txt", "%E6%96%87%E4%BB%B6.txt" })
            await check("Private newly consented native search and content flow preserves exact model arguments " + query, async () =>
            {
                string search = $"/drives/{NativeDriveId}/items/root/search(q='{query}')?$select=id,name,file,folder,size,parentReference&$top=20";
                string blob = $"/drives/{NativeDriveId}/items/found/content";
                await using PrivateHarness p = new(new(
                    Completion(tool: "fetch", args: Fetch(search)),
                    Completion(tool: "fetch_blob", args: new { path = blob }, id: "blob"),
                    Completion("Observed synthetic sample content.")));
                p.H.Handler.SectionTwoResults[search] = new()
                { ["value"] = new JsonArray(new JsonObject { ["id"] = "found", ["name"] = "sample.txt" }) };
                p.H.Handler.BlobBytes = Encoding.UTF8.GetBytes("Synthetic private sample.");
                PrivateJob job = await p.Initiate();
                Must(job.ConsentScope == PrivateConsentScope.NativeReadWriteV1 && p.H.Handler.ToolCalls == 0);
                string card = p.Ui.Wire.Requests[1].Body.GetRawText();
                Must(card.Contains("NativeReadWriteV1") && card.Contains("delete") && card.Contains("share") &&
                    card.Contains("send") && card.Contains("no per-tool confirmation"), card);
                JsonObject invoke = PrivateInvoke(p, job);
                invoke["conversation"]!["conversationType"] = "groupChat"; invoke["conversation"]!["isGroup"] = true;
                var approval = await InvokeWire(p, invoke);
                Must(approval.Body.GetProperty("statusCode").GetInt32() == 200);
                string url = approval.Body.GetProperty("value").GetProperty("actions")[0].GetProperty("url").GetString()!;
                Must(approval.Body.GetRawText().Contains("NativeReadWriteV1") && p.Model.Requests.Count == 0);
                await p.Authenticate(url);
                await p.Worker.RunOne(default);
                Must(p.Store.Get(job.Id).Result!.Contains("Observed synthetic sample content.") &&
                    p.Store.Get(job.Id).Result!.Contains("PRIVATE HUMAN NATIVE WORK") &&
                    p.H.Handler.Calls[0].Args.GetProperty("entityUrls")[0].GetString() == search &&
                    p.H.Handler.Calls[1].Args.GetProperty("path").GetString() == blob && p.H.Handler.ToolCalls == 2,
                    p.Store.Get(job.Id).Result ?? p.Store.Get(job.Id).State.ToString());
                Must(p.Model.Requests[2].GetProperty("messages").EnumerateArray().Any(m =>
                    m.TryGetProperty("content", out JsonElement text) && text.ValueKind == JsonValueKind.String &&
                    text.GetString()!.Contains("Synthetic private sample.")));
                foreach (string bearer in p.H.Handler.Bearers)
                    p.H.HumanSettings.ValidateAccessToken(bearer, p.H.HumanSettings.Key(
                        p.H.Policy.Authorize(p.Personal, p.H.Settings)), Harness.Principal(Harness.Human));
                Must(!await p.Worker.RunOne(default));
            });
        foreach (string tool in new[] { "create_entity", "update_entity", "delete_entity", "do_action", "custom_operation" })
            await check("Private native write or action requires new fixed human job approval " + tool, async () =>
            {
                object args = tool switch
                {
                    "create_entity" => CreateArgs(),
                    "do_action" => new { actionUrl = "/native/action", jsonBody = "{}" },
                    "custom_operation" => new { jsonBody = "{}" },
                    _ => new { entityUrl = "/drives/drive/items/file", jsonBody = "{}" }
                };
                await using PrivateHarness p = new(new(Completion(tool: tool, args: args), Completion("Native operation returned.")));
                PrivateJob job = await p.Initiate();
                Must(!await p.Worker.RunOne(default) && p.Model.Requests.Count == 0 && p.H.Handler.Mutations == 0);
                string link = await p.Approve(job);
                Must(!await p.Worker.RunOne(default) && p.H.Handler.Mutations == 0);
                await p.Authenticate(link);
                await p.Worker.RunOne(default);
                Must(p.H.Handler.Mutations == 1 && p.H.Handler.ToolCalls == 1 &&
                    JsonElement.DeepEquals(p.H.Handler.Calls.Single().Args, Json(args)) &&
                    p.Store.Get(job.Id).Result!.Contains("Native operation returned."), p.Store.Get(job.Id).Result!);
                Must(!await p.Worker.RunOne(default));
                var duplicate = await InvokeWire(p, PrivateInvoke(p, job));
                Must(duplicate.Body.GetProperty("statusCode").GetInt32() == 400 && p.H.Handler.Mutations == 1);
            });
        foreach (string path in new[] { "/me/messages?$filter=isRead%20eq%20false", "/me/drive/root/search(q='x')",
            "/drives/drive/items/root/search(q='bad'unescaped')?$skiptoken=service-validates",
            "/native/function(q='grant')", "/users/other/drive" })
            await check("Private new scope does not impose local path query or function grammar " + path, async () =>
            {
                await using PrivateHarness p = new(new(Completion(tool: "fetch", args: Fetch(path)), Completion("Service returned.")));
                p.H.Handler.SectionTwoResults[path] = new() { ["fixture"] = "synthetic-service-not-schema-validation" };
                PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
                await p.Worker.RunOne(default);
                Must(p.H.Handler.ToolCalls == 1 && p.H.Handler.Calls[0].Args.GetProperty("entityUrls")[0].GetString() == path);
            });
        foreach (bool legacy in new[] { false, true })
            await check("Private catalog matches selected immutable scope not inferred request text " + legacy, async () =>
            {
                await using PrivateHarness p = new(new(Completion("No operation needed.")));
                PrivateJob job = await p.Initiate(); if (legacy) job = p.Legacy(job);
                await p.Authenticate(await p.Approve(job)); await p.Worker.RunOne(default);
                string[] names = p.Model.Requests.Single().GetProperty("tools").EnumerateArray()
                    .Select(t => t.GetProperty("function").GetProperty("name").GetString()!).Order().ToArray();
                WorkIqTool[] raw = (await p.H.AuSession.ListAsync(default)).Tools.ToArray();
                string[] expected = raw.Where(t => !DirectMcpContract.Excluded(t) && (!legacy || PrivateReadContract.Offered(t)))
                    .Select(t => DirectMcpContract.ForModel(t).Name).Append(WorkIqSkill.LoadTool)
                    .Concat(legacy ? [] : new[] { DirectContinuationHandles.ToolName }).Order().ToArray();
                Must(names.SequenceEqual(expected) && !names.Contains("ask") && !names.Contains("send_user_mention") &&
                    names.Contains("create_entity") == !legacy);
            });
        foreach (string key in new[] { "agentId", "authorization", "headers", "accessToken", "endpoint", "callbackUrl" })
            await check("Private native scope preserves recursive identity and transport override denial " + key, async () =>
            {
                string body = new JsonObject { ["nested"] = new JsonArray(new JsonObject { [key] = "PRIVATE-secret" }) }.ToJsonString();
                await using PrivateHarness p = new(new(Completion(tool: "create_entity",
                    args: new { parentUrl = "/native", jsonBody = body })));
                PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
                await p.Worker.RunOne(default);
                Must(p.H.Handler.ToolCalls == 0 && p.H.Handler.Mutations == 0 && p.Model.Requests.Count == 1 &&
                    p.Store.Get(job.Id).Result!.Contains("override denied") &&
                    (key is not ("authorization" or "headers" or "accessToken") || !p.Store.Get(job.Id).Result!.Contains("PRIVATE-secret")),
                    p.Store.Get(job.Id).Result!);
            });
        await check("Private native write result delivery recovery never repeats the operation", async () =>
        {
            await using PrivateHarness p = new(new(Completion(tool: "create_entity", args: CreateArgs()), Completion("Write returned.")));
            PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
            p.Ui.Wire.Respond = (_, _) => Task.FromResult(p.Ui.Wire.Requests.Last().Body.TryGetProperty("text", out _)
                ? UiTransport.Fail(HttpStatusCode.BadRequest) : UiTransport.Ok());
            await p.Worker.RunOne(default);
            Must(p.Store.Get(job.Id).State == PrivateJobState.DeliveryPending && p.H.Handler.Mutations == 1);
            int models = p.Model.Requests.Count;
            p.Ui.Wire.Respond = (_, _) => Task.FromResult(UiTransport.Ok());
            await p.Coordinator.Handle(p.Personal, default);
            PrivateJob pending = p.Store.Get(job.Id);
            Must(pending.ConsentScope == PrivateConsentScope.NativeReadWriteV1 &&
                p.Ui.Wire.Requests.Last().Body.GetRawText().Contains("no execution replay"));
            await p.Coordinator.Action(p.Personal, "lab13.private.approve", pending.Id, pending.Revision, default);
            await p.Worker.RunOne(default);
            Must(p.Store.Get(job.Id).State == PrivateJobState.Completed && p.H.Handler.Mutations == 1 &&
                p.Model.Requests.Count == models);
        });
        foreach (string failure in new[] { "timeout", "http", "resource" })
            await check("Private native failed write stops remaining proposals and durable execution never replays " + failure, async () =>
            {
                await using PrivateHarness p = new(new(TwoWrites()));
                if (failure == "timeout") p.H.Handler.MutationTimeout = true;
                if (failure == "http") p.H.Handler.MutationHttpStatus = HttpStatusCode.BadGateway;
                if (failure == "resource") p.H.Handler.MutationResourceStatus = 400;
                PrivateJob job = await p.Initiate(); await p.Authenticate(await p.Approve(job));
                await p.Worker.RunOne(default);
                Must(p.H.Handler.Mutations == 1 && p.H.Handler.ToolCalls == 1 && p.Model.Requests.Count == 1 &&
                    p.Store.Get(job.Id).HasRun && !await p.Worker.RunOne(default));
                Must(p.Store.Get(job.Id).Result!.Contains(failure == "resource" ? "effects may be partial" : "UNKNOWN OUTCOME"));
                using PrivateJobStore restarted = new(new MemoryPrivateStorage { Bytes = p.Storage.Bytes!.ToArray() }, p.H.Clock);
                Must(restarted.Get(job.Id).Terminal && restarted.Get(job.Id).HasRun);
            });
        await check("Private legacy scope immutable grants and missing-scope restart cannot gain writes", async () =>
        {
            await using PrivateHarness p = new(new(Completion(tool: "create_entity", args: CreateArgs())));
            PrivateJob legacy = p.Legacy(await p.Initiate());
            string ticketUrl = await p.Approve(legacy);
            PrivateJob approved = p.Store.Get(legacy.Id);
            PrivateGrant grant = p.Authorization.Grant(approved);
            Must(grant.ConsentScope == PrivateConsentScope.LegacyReadOnly);
            byte[] snapshot = p.Storage.Bytes!.ToArray();
            await Denied(() => { p.Store.Change(legacy.Id, j => j with { ConsentScope = PrivateConsentScope.NativeReadWriteV1 }); return Task.CompletedTask; });
            Must(snapshot.SequenceEqual(p.Storage.Bytes!));
            await Denied(() =>
            {
                p.Authorization.ValidateGrant(grant with { ConsentScope = PrivateConsentScope.NativeReadWriteV1 },
                    p.H.HumanSettings.Key(p.H.Policy.Authorize(p.Personal, p.H.Settings)), grant.PersonalBinding);
                return Task.CompletedTask;
            });
            await p.Authenticate(ticketUrl); await p.Worker.RunOne(default);
            Must(p.H.Handler.Mutations == 0 && p.H.Handler.ToolCalls == 0);

            JsonObject old = JsonSerializer.SerializeToNode(new Dictionary<string, PrivateJob> { [legacy.Id] = approved })!.AsObject();
            old[legacy.Id]!.AsObject().Remove("ConsentScope");
            using PrivateJobStore restarted = new(new MemoryPrivateStorage { Bytes = Encoding.UTF8.GetBytes(old.ToJsonString()) }, p.H.Clock);
            PrivateJob restored = restarted.Get(legacy.Id);
            Must(restored.ConsentScope == PrivateConsentScope.LegacyReadOnly && restored.Revision > approved.Revision &&
                restored.State == PrivateJobState.AwaitingApproval && restored.HumanGeneration is null);
            PrivateAuthorizations auth = new(restarted, p.Options, p.H.Policy, p.H.Settings, p.H.HumanSettings, p.ModelOptions, p.H.Clock);
            await Denied(() =>
            {
                auth.ValidateGrant(grant, p.H.HumanSettings.Key(p.H.Policy.Authorize(p.Personal, p.H.Settings)), grant.PersonalBinding);
                return Task.CompletedTask;
            });
            await using HumanConnections humans = new(p.H.HumanSettings, p.H.Cache,
                new(tokens => new WorkIqSession(tokens, p.H.Handler)), p.H.Clock, auth);
            await Denied(() => { humans.Begin(new Uri(ticketUrl).Query["?ticket=".Length..]); return Task.CompletedTask; });
            PrivateAnalysisCoordinator coordinator = new(p.Options, restarted, auth, humans, p.H.HumanSettings, p.Teams,
                new(p.ModelOptions, o => new NaturalLanguageModel(o, p.Routing)), p.H.Policy, p.H.Settings, p.H.Clock, p.Log);
            await coordinator.Handle(p.Personal, default);
            restored = restarted.Get(legacy.Id);
            Must(restored.ConsentScope == PrivateConsentScope.LegacyReadOnly &&
                p.Ui.Wire.Requests.Last().Body.GetRawText().Contains("LegacyReadOnly"));
            PrivateActionResult response = await coordinator.Action(p.Personal, "lab13.private.approve", restored.Id, restored.Revision, default);
            string url = response.Card!.Value.GetProperty("actions")[0].GetProperty("url").GetString()!;
            string flow = humans.Begin(new Uri(url).Query["?ticket=".Length..]);
            await humans.Complete(flow, Harness.Principal(Harness.Human), default);
            await Denied(() => humans.Complete(flow, Harness.Principal(Harness.Human), default));
            ModelHandler model = new(Completion(tool: "delete_entity", args: new { entityUrl = "/drives/drive/items/file", jsonBody = "{}" }));
            using PrivateAnalysisWorker worker = new(restarted, auth, humans,
                new(p.H.AuSession, humans, p.H.HumanSettings, p.H.Settings), p.Teams,
                p.ModelOptions, p.H.Policy, p.H.Settings, new(), new(), p.H.Clock,
                NullLogger<PrivateAnalysisWorker>.Instance, o => new NaturalLanguageModel(o, model));
            await worker.RunOne(default);
            Must(p.H.Handler.Mutations == 0 && p.H.Handler.ToolCalls == 0 &&
                restarted.Get(legacy.Id).Result!.Contains("LEGACY READ-ONLY"));
        });
    }
}
