using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static async Task PrivateBoundInvokeChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (string variant in new[] { "group", "tenant", "empty-tenant", "team-empty", "team-populated",
            "channel-empty", "channel-populated", "team-alias-empty", "team-alias-populated",
            "channel-alias-empty", "channel-alias-populated", "multiple", "null-defaults" })
            await check("Private metadata conflict evidence distinguishes every SDK-native contributing field " + variant, async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob job = await p.Initiate();
                JsonObject input = PrivateInvoke(p, job);
                input["conversation"]!["conversationType"] = variant == "group" ? "personal" : "groupChat";
                input["conversation"]!.AsObject().Remove("isGroup");
                input["conversation"]!["tenantId"] = Harness.Tenant;
                string expected = variant switch
                {
                    "group" => "ExplicitGroupFlag",
                    "multiple" => "TeamMetadata",
                    "tenant" or "empty-tenant" => "ConversationTenantMismatch",
                    "team-empty" or "team-populated" => "TeamMetadata",
                    "channel-empty" or "channel-populated" => "ChannelMetadata",
                    "team-alias-empty" or "team-alias-populated" => "TeamAliasMetadata",
                    _ => "ChannelAliasMetadata"
                };
                switch (variant)
                {
                    case "group": input["conversation"]!["isGroup"] = true; break;
                    case "tenant": input["conversation"]!["tenantId"] = Harness.Other; break;
                    case "empty-tenant": input["conversation"]!["tenantId"] = ""; break;
                    case "team-empty": input["channelData"]!["team"] = new JsonObject(); break;
                    case "team-populated": input["channelData"]!["team"] = new JsonObject { ["id"] = "PRIVATE-team" }; break;
                    case "channel-empty": input["channelData"]!["channel"] = new JsonObject(); break;
                    case "channel-populated": input["channelData"]!["channel"] = new JsonObject { ["id"] = "PRIVATE-channel" }; break;
                    case "team-alias-empty": input["channelData"]!["teamsTeamId"] = ""; break;
                    case "team-alias-populated": input["channelData"]!["teamsTeamId"] = "PRIVATE-team"; break;
                    case "channel-alias-empty": input["channelData"]!["teamsChannelId"] = ""; break;
                    case "channel-alias-populated": input["channelData"]!["teamsChannelId"] = "PRIVATE-channel"; break;
                    case "multiple":
                        input["conversation"]!["isGroup"] = true;
                        input["conversation"]!["tenantId"] = Harness.Other;
                        input["channelData"]!["team"] = new JsonObject { ["id"] = "PRIVATE-team" };
                        input["channelData"]!["teamsChannelId"] = "PRIVATE-channel"; break;
                    default:
                        input["conversation"]!["isGroup"] = null;
                        input["conversation"]!["tenantId"] = null;
                        foreach (string key in new[] { "team", "channel", "teamsTeamId", "teamsChannelId" })
                            input["channelData"]![key] = null;
                        break;
                }
                var response = await InvokeWire(p, input);
                if (variant == "null-defaults")
                {
                    Must(response.Body.GetProperty("statusCode").GetInt32() == 200 &&
                        !p.Log.Captured.Lines.Any(l => l.StartsWith("Private ingress diagnostic:")));
                    return;
                }
                Must(response.Status == 200 && response.Body.GetProperty("statusCode").GetInt32() == 400, variant);
                string log = p.Log.Captured.Lines.Single(l => l.StartsWith("Private ingress diagnostic:"));
                string arrived = p.Log.Captured.Lines.Single(l => l.Contains("event=Arrived"));
                string correlation = arrived.Split("correlation=")[1].Split(';')[0];
                Must(log.Contains("correlation=" + correlation) && log.Contains("policyReference=IncomingAction") &&
                    log.Contains("metadataConflict=" + expected) && log.Contains("reason=ConflictingPrivateAudienceMetadata"), log);
                if (variant is "group" or "multiple") Must(log.Contains("isGroup=true"), log);
                else Must(log.Contains("isGroup=unavailable"), log);
                if (variant is "tenant" or "empty-tenant" or "multiple") Must(log.Contains("conversationTenantMatches=false"), log);
                else Must(log.Contains("conversationTenantMatches=true"), log);
                if (variant is "team-empty" or "channel-empty")
                    Must(log.Contains((variant == "team-empty" ? "teamShape=" : "channelShape=") + "NullOnlyObject"), log);
                if (variant.Contains("alias-empty")) Must(log.Contains("AliasShape=EmptyString"), log);
                if (variant is "team-populated" or "multiple") Must(log.Contains("teamShape=Object"), log);
                if (variant == "channel-populated") Must(log.Contains("channelShape=Object"), log);
                if (variant.Contains("alias-populated") || variant == "multiple") Must(log.Contains("AliasShape=String"), log);
                Must(!log.Contains("PRIVATE") && !log.Contains(Harness.Tenant) && !log.Contains(Harness.Other) &&
                    !log.Contains(job.Id) && !log.Contains("https://") && p.Store.Get(job.Id).Revision == job.Revision &&
                    p.Ui.Wire.Requests.Count == 2 && p.H.Handler.ToolCalls == 0 && p.Model.Requests.Count == 0);
            });
        foreach (bool? groupFlag in new bool?[] { null, false, true })
            await check("Private native groupChat-labeled invoke uses only established personal route " + groupFlag, async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob job = await p.Initiate();
                JsonObject input = PrivateInvoke(p, job);
                input["conversation"]!["conversationType"] = "groupChat";
                if (groupFlag is { } flag) input["conversation"]!["isGroup"] = flag;
                else input["conversation"]!.AsObject().Remove("isGroup");
                input["replyToId"] = job.MessageId;
                var first = await InvokeWire(p, input);
                Must(first.Status == 200 && first.Body.GetProperty("statusCode").GetInt32() == 200, first.Body.ToString());
                string url = first.Body.GetProperty("value").GetProperty("actions")[0].GetProperty("url").GetString()!;
                PrivateJob approved = p.Store.Get(job.Id);
                Must(approved.State == PrivateJobState.AwaitingSignIn && approved.Revision == job.Revision + 1 &&
                    JsonElement.DeepEquals(approved.Personal!.Value, job.Personal!.Value) &&
                    input["conversation"]!["conversationType"]!.GetValue<string>() == "groupChat" &&
                    p.Ui.Wire.Requests.Count == 2 && p.Model.Requests.Count == 0 && p.H.Handler.ToolCalls == 0);
                Must(p.Log.Captured.Lines.Any(l => l.Contains("inner=200") &&
                    l.Contains("boundCallbackGroupFlagTrusted=" + (groupFlag == true))));
                var duplicate = await InvokeWire(p, input);
                Must(duplicate.Body.GetProperty("value").GetProperty("actions")[0].GetProperty("url").GetString() == url &&
                    p.Store.Get(job.Id).Revision == approved.Revision);
                await p.Authenticate(url);
                await p.Worker.RunOne(default);
                Must(p.Store.Get(job.Id).State == PrivateJobState.Completed && p.H.Handler.ToolCalls == 1 &&
                    p.Model.Requests.Count == 2);
                foreach (var request in p.Ui.Wire.Requests.Where(r => r.Method == HttpMethod.Put))
                    Must(Uri.UnescapeDataString(request.Url.AbsolutePath).Contains(p.Personal.Conversation!.Id) &&
                        request.Identity?.AgenticUserId == Harness.Au,
                        "All result delivery must target the established private route.");
                foreach (string bearer in p.H.Handler.Bearers)
                    p.H.HumanSettings.ValidateAccessToken(bearer,
                        p.H.HumanSettings.Key(p.H.Policy.Authorize(p.Personal, p.H.Settings)), Harness.Principal(Harness.Human));
            });
        foreach (string difference in new[] { "chat", "owner", "tenant", "au", "team", "channel",
            "teamsChannelId", "teamsTeamId", "conversationTenant", "revision", "unknown-job",
            "stored-group", "stored-flag", "missing-card", "channel-id", "service", "extra-destination", "automatic", "expired" })
            await check("Private bound native invoke denies conflicting authority without consuming approval " + difference, async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob job = await p.Initiate();
                JsonObject input = PrivateInvoke(p, job);
                input["conversation"]!["conversationType"] = "groupChat";
                input["conversation"]!["isGroup"] = true;
                switch (difference)
                {
                    case "chat": input["conversation"]!["id"] = "19:genuine-other-group"; break;
                    case "owner": input["from"]!["aadObjectId"] = Harness.Other; break;
                    case "tenant": input["channelData"]!["tenant"]!["id"] = Harness.Other; break;
                    case "au": input["recipient"]!["agenticUserId"] = Harness.Other; break;
                    case "team": input["channelData"]!["team"] = new JsonObject(); break;
                    case "channel": input["channelData"]!["channel"] = new JsonObject(); break;
                    case "teamsChannelId": input["channelData"]!["teamsChannelId"] = "19:PRIVATE"; break;
                    case "teamsTeamId": input["channelData"]!["teamsTeamId"] = Harness.Other; break;
                    case "conversationTenant": input["conversation"]!["tenantId"] = Harness.Other; break;
                    case "revision": input["value"]!["action"]!["data"]!["revision"] = job.Revision + 1; break;
                    case "unknown-job": input["value"]!["action"]!["data"]!["job"] = new string('a', 48); break;
                    case "channel-id": input["channelId"] = "PRIVATE-other-channel"; break;
                    case "service": input["serviceUrl"] = "https://other.example.invalid/"; break;
                    case "extra-destination": input["value"]!["action"]!["data"]!["destination"] = "PRIVATE"; break;
                    case "automatic": input["value"]!["trigger"] = "automatic"; break;
                    case "expired": p.H.Clock.Now += TimeSpan.FromMinutes(31); break;
                    case "missing-card": p.Store.Change(job.Id, j => j with { MessageId = null }); break;
                    default:
                        JsonObject stored = JsonNode.Parse(job.Personal!.Value.GetRawText())!.AsObject();
                        if (difference == "stored-group") stored["conversation"]!["conversationType"] = "groupChat";
                        else stored["conversation"]!["isGroup"] = true;
                        p.Store.Change(job.Id, j => j with { Personal = JsonSerializer.SerializeToElement(stored) });
                        break;
                }
                var response = await InvokeWire(p, input, authenticatedServiceUrl:
                    difference == "service" ? "https://other.example.invalid/" : null);
                Must(response.Status == 200 && response.Body.GetProperty("statusCode").GetInt32() == 400, difference);
                Must(p.Ui.Wire.Requests.Count == 2 && p.Model.Requests.Count == 0 && p.H.Handler.ToolCalls == 0 &&
                    !response.Body.ToString().Contains("https://"));
                if (difference != "expired") Must(p.Store.Get(job.Id).Revision == job.Revision);
                Must(!string.Join("\n", p.Log.Captured.Lines).Contains("PRIVATE"));
            });
        await check("Private native type compatibility never applies to ordinary messages or direct group authorization", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate();
            p.Personal.Conversation!.ConversationType = "groupChat";
            p.Personal.Conversation.IsGroup = true;
            await Denied(() => p.Coordinator.Action(p.Personal, "lab13.private.approve", job.Id, job.Revision, default));
            await Denied(() => { p.H.Policy.Authorize(p.Personal, p.H.Settings); return Task.CompletedTask; });
            Must(p.Store.Get(job.Id).Revision == job.Revision && p.H.Handler.ToolCalls == 0);
        });
        await check("Private native groupChat-labeled cancel revokes bound job without starting sign-in or analysis", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate();
            JsonObject input = PrivateInvoke(p, job, "lab13.private.cancel");
            input["conversation"]!["conversationType"] = "groupChat";
            var response = await InvokeWire(p, input);
            Must(response.Status == 200 && response.Body.GetProperty("statusCode").GetInt32() == 200 &&
                p.Store.Get(job.Id).State == PrivateJobState.Cancelled && p.Model.Requests.Count == 0 && p.H.Handler.ToolCalls == 0);
            var duplicate = await InvokeWire(p, input);
            Must(duplicate.Body.GetProperty("statusCode").GetInt32() == 400);
        });
        await check("Private reference snapshot retains contradictory group metadata for rejection on reload", () =>
        {
            var personal = ProgressActivity("/private-jobs", "personal");
            personal.Conversation!.IsGroup = true; personal.Conversation.TenantId = Harness.Other;
            var restored = PrivateContext.Activity(PrivateContext.Capture(personal));
            Must(restored.Conversation!.IsGroup == true && restored.Conversation.TenantId == Harness.Other);
            return Task.CompletedTask;
        });
        await check("Private bound invoke cannot approve another authorized owners job in the same configured chat", async () =>
        {
            await using PrivateHarness p = new();
            PrivateJob job = await p.Initiate();
            var origin = PrivateContext.Activity(job.Origin);
            var personal = PrivateContext.Activity(job.Personal!.Value);
            origin.From!.AadObjectId = Harness.Other; personal.From!.AadObjectId = Harness.Other;
            PrivateJob other = p.Store.Add(job with { Id = new string('b', 48), Owner = Harness.Other,
                Origin = PrivateContext.Capture(origin), Personal = PrivateContext.Capture(personal) });
            p.Authorization.Check(other);
            JsonObject input = PrivateInvoke(p, other);
            input["conversation"]!["conversationType"] = "groupChat";
            var response = await InvokeWire(p, input);
            Must(response.Body.GetProperty("statusCode").GetInt32() == 400 &&
                p.Store.Get(other.Id).Revision == other.Revision && p.Store.Get(job.Id).Revision == job.Revision &&
                p.H.Handler.ToolCalls == 0 && p.Ui.Wire.Requests.Count == 2);
        });
    }
}
