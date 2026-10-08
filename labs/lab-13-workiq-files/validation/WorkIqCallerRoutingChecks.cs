using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private const string ExplicitAuAttachmentPrompt =
        "Using the configured AU identity and WorkIQ only, read the attached file and summarize its main points. " +
        "Use the attachment details to identify the file; do not substitute a different file. " +
        "If you cannot identify or access it, explain what is missing. Do not use direct Graph SDK or Teams MCP.";

    private static async Task WorkIqCallerRoutingChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (string original in new[]
        {
            ExplicitAuAttachmentPrompt,
            "Read the attachment with the agent user's WorkIQ access, not my human sign-in, and give its main points.",
            "Use AU access to read my OneDrive file attached here. Keep that caller even if it lacks access; explain the limitation."
        })
            await check("Installed explicit AU attachment routing preserves request and continues native profile " + original, async () =>
            {
                const string path = "/drives/fixture-drive/items/fixture-file";
                await using PrivateHarness p = new(
                    model: new(Completion(tool: "fetch", args: Fetch(path)), Completion("Metadata only; file content is not established.")),
                    routing: new(Completion(tool: "continue_current_profile", args: new { })));
                p.Origin.Text = original;
                p.Origin.Attachments = AttachedActivity(original, new JsonArray(FileAttachment())).Attachments;
                p.H.Handler.SectionTwoResults[path] = new JsonObject { ["id"] = "fixture-file", ["name"] = "sample2.txt" };
                PrivateIngress route = await p.Coordinator.Handle(p.Origin, default);
                Must(!route.Handled && p.Store.List().Length == 0 && p.Ui.Wire.Requests.Count == 0 &&
                    p.H.Handler.ToolCalls == 0 && p.H.Handler.CatalogCalls == 0);
                JsonElement request = p.Routing.Requests.Single();
                string system = request.GetProperty("messages")[0].GetProperty("content").GetString()!;
                Must(request.GetProperty("messages")[1].GetProperty("content").GetString() == original &&
                    system.Contains("configured DirectMcp DATA principal=AgentUser") &&
                    system.Contains("Explicit DATA caller instructions take precedence over ownership-based inference") &&
                    system.Contains("Missing attachment details or denied access") &&
                    system.Contains("attachment/file analysis does not imply human authentication"));
                JsonElement[] tools = request.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function")).ToArray();
                Must(tools.Length == 3 && tools.All(t => t.GetProperty("name").GetString() != "report_profile_mismatch"));
                string continuation = tools.Single(t => t.GetProperty("name").GetString() == "continue_current_profile")
                    .GetProperty("description").GetString()!;
                Must(continuation.Contains("explicit AU attachment/file analysis") &&
                    !continuation.Contains("Do not choose this for private-file analysis"));
                FullGuideRequests(p.Routing, WorkIqGuideMode.RoutingOnly);
                string output = (await App(p.H, p.Model, p.ModelOptions).Handle(p.Origin, default))!;
                Must(output.Contains("Metadata only") && p.H.Handler.Calls.Single().Tool == "fetch" &&
                    p.H.Handler.Mutations == 0 && MessageMetadata(p.Model.Requests[0]).GetProperty("attachmentCount").GetInt32() == 1);
                FullGuideRequests(p.Model, WorkIqGuideMode.Direct);
                Must(p.Model.Requests[0].GetProperty("messages").EnumerateArray().Any(m =>
                    m.GetProperty("role").GetString() == "user" &&
                    m.GetProperty("content").ValueKind == JsonValueKind.String && m.GetProperty("content").GetString() == original));
                foreach (string token in p.H.Handler.Bearers) AgentUserTokenProvider.ValidateTokenShape(token, p.H.Settings);
            });
        await check("Installed explicit AU under human profile reports unavailable without reconfirmation or fallback", async () =>
        {
            await using PrivateHarness p = new(routing: new(Completion(tool: "report_profile_mismatch", args: new { })));
            p.ModelOptions.DirectMcp.Principal = "SignedInHuman";
            p.Origin.Text = ExplicitAuAttachmentPrompt;
            PrivateIngress result = await p.Coordinator.Handle(p.Origin, default);
            Must(result.Handled && result.Reply!.Contains("caller/profile mismatch") &&
                result.Reply.Contains("SignedInHuman") && !result.Reply.Contains("needs clarification") &&
                p.Store.List().Length == 0 && p.H.Handler.CatalogCalls == 0 && p.H.Handler.ToolCalls == 0 &&
                p.Ui.Wire.Requests.Count == 0 && p.Model.Requests.Count == 0);
            JsonElement request = p.Routing.Requests.Single();
            Must(request.GetProperty("messages")[0].GetProperty("content").GetString()!
                .Contains("configured DirectMcp DATA principal=SignedInHuman") &&
                request.GetProperty("messages")[1].GetProperty("content").GetString() == ExplicitAuAttachmentPrompt);
            JsonElement tool = request.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function"))
                .Single(t => t.GetProperty("name").GetString() == "report_profile_mismatch");
            Must(tool.GetProperty("parameters").GetProperty("properties").EnumerateObject().Count() == 0 &&
                !tool.GetProperty("parameters").GetProperty("additionalProperties").GetBoolean());
            FullGuideRequests(p.Routing, WorkIqGuideMode.RoutingOnly);
        });
        foreach (string profile in new[] { "AgentUser", "SignedInHuman" })
            await check("Explicit human data caller still proposes private approval, independently of coordination " + profile, async () =>
            {
                await using PrivateHarness p = new();
                p.ModelOptions.DirectMcp.Principal = profile;
                p.Origin.Text = "Read the attachment with my human WorkIQ sign-in; the AU only coordinates.";
                PrivateJob job = await p.Initiate();
                Must(job.State == PrivateJobState.AwaitingApproval && job.Request == p.Origin.Text &&
                    p.H.Handler.ToolCalls == 0 && p.Model.Requests.Count == 0);
                FullGuideRequests(p.Routing, WorkIqGuideMode.RoutingOnly);
            });
        await check("Explicit AU wording does not trigger a parser or override model clarification", async () =>
        {
            const string question = "Synthetic model clarification retained without rewriting.";
            await using PrivateHarness p = new(routing: new(Completion(tool: "clarify_workiq", args: new { question })));
            p.Origin.Text = ExplicitAuAttachmentPrompt;
            PrivateIngress result = await p.Coordinator.Handle(p.Origin, default);
            Must(result.Handled && result.Reply!.Contains(question) &&
                p.Store.List().Length == 0 && p.H.Handler.ToolCalls == 0);
        });
        foreach (string profile in new[] { "AgentUser", "SignedInHuman" })
            await check("Mismatch proposals cannot inject an identity or execute unsupported tool " + profile, async () =>
            {
                await using PrivateHarness p = new(routing: new(Completion(tool: "report_profile_mismatch",
                    args: profile == "AgentUser" ? new { } : (object)new { principal = "AgentUser" })));
                p.ModelOptions.DirectMcp.Principal = profile;
                await Denied(async () => await p.Coordinator.Handle(p.Origin, default));
                Must(p.Store.List().Length == 0 && p.H.Handler.ToolCalls == 0);
            });
    }
}
