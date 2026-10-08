using System.Net;
using System.Text.Json;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static async Task WorkIqNativeEntryChecks(Func<string, Func<Task>, Task> check)
    {
        foreach (string missing in new[] { "Enabled", "Direct", "Guide" })
            await check("WorkIQ native activation fails before model/catalog at ordinary and private ingress " + missing, async () =>
            {
                await using PrivateHarness p = new();
                if (missing == "Enabled") p.ModelOptions.Enabled = false;
                if (missing == "Direct") p.ModelOptions.DirectMcp.Enabled = false;
                if (missing == "Guide") p.ModelOptions.FullWorkIqGuide = false;
                string ordinary = (await App(p.H, p.Model, p.ModelOptions).Handle(p.Origin, default))!;
                PrivateIngress routed = await p.Coordinator.Handle(p.Origin, default);
                Must(ordinary.Contains(NaturalLanguageOptions.WorkIqActivation) &&
                    routed.Handled && routed.Reply == NaturalLanguageOptions.WorkIqActivation &&
                    p.Store.List().Length == 0 && p.Model.Requests.Count == 0 && p.Routing.Requests.Count == 0 &&
                    p.H.Handler.CatalogCalls == 0 && p.H.Handler.ToolCalls == 0 && p.Ui.Wire.Requests.Count == 0);
            });
        foreach (string text in new[]
        {
            "Please put a note saying Don't rewrite <b>this</b> & keep \"quotes\" in the channel.",
            "Could you send the same note for us? No special command syntax.",
            "I would like that channel note posted; use the AU data account."
        })
            await check("Installed WorkIQ original paraphrase and native model-selected order remain intact " + text, async () =>
            {
                await using Harness h = new(); DirectEnable(h);
                const string body = """{ "body" : {"content":"Don't rewrite <b>this</b> & keep \"quotes\"","contentType":"html"} }""";
                var native = new { parentUrl = "/native/messages", jsonBody = body };
                ModelHandler model = new(Completion(tool: "create_entity", args: native),
                    Completion(tool: "fetch", args: Fetch(Team), id: "read-after-write"), Completion("Native results received."));
                var activity = Harness.Activity(text);
                Must(!WorkIqIngress.IsOperationalOrSlash(activity));
                string output = (await App(h, model, DirectOptions()).Handle(activity, default))!;
                Must(output.Contains("Native results received.") && h.Handler.Calls.Select(c => c.Tool)
                    .SequenceEqual(["create_entity", "fetch"]), output);
                Must(h.Handler.Calls[0].Args.GetProperty("jsonBody").GetString() == body);
                foreach (JsonElement request in model.Requests)
                    Must(request.GetProperty("messages").EnumerateArray().Any(m =>
                        m.GetProperty("role").GetString() == "user" && m.GetProperty("content").GetString() == activity.Text));
                FullGuideRequests(model, WorkIqGuideMode.Direct);
            });
        foreach (string text in new[] { "Use my data account or the AU's account; I haven't decided.",
            "Use only my identity and only the different AU identity to read the same file." })
            await check("WorkIQ semantic identity clarification has no job identity switch or data dispatch " + text, async () =>
            {
                const string question = "Should the data caller be your signed-in account or the configured AU?";
                await using PrivateHarness p = new(routing: new(Completion(tool: "clarify_workiq", args: new { question })));
                p.Origin.Text = text;
                PrivateIngress result = await p.Coordinator.Handle(p.Origin, default);
                Must(result.Handled && result.Reply!.Contains(question) && p.Store.List().Length == 0 &&
                    p.H.Handler.ToolCalls == 0 && p.H.Handler.CatalogCalls == 0 && p.Ui.Wire.Requests.Count == 0);
                FullGuideRequests(p.Routing, WorkIqGuideMode.RoutingOnly);
                JsonElement request = p.Routing.Requests.Single();
                Must(request.GetProperty("messages")[1].GetProperty("content").GetString() == text);
                string instructions = request.GetProperty("messages")[0].GetProperty("content").GetString()!;
                Must(instructions.Contains("compatible roles") && instructions.Contains("Do not reconfirm complete explicit intent"));
            });
        foreach (object args in new object[] { new { }, new { question = "" },
            new { question = "Which caller?", principal = "AgentUser" } })
            await check("WorkIQ clarification rejects invalid structured model arguments without repair " + JsonSerializer.Serialize(args), async () =>
            {
                await using PrivateHarness p = new(routing: new(Completion(tool: "clarify_workiq", args: args)));
                await Denied(async () => await p.Coordinator.Handle(p.Origin, default));
                Must(p.Store.List().Length == 0 && p.H.Handler.ToolCalls == 0);
            });
        foreach (string missing in new[] { "Enabled", "Direct", "Guide" })
            await check("Private worker cannot silently reenable native full-guide execution " + missing, async () =>
            {
                await using PrivateHarness p = new();
                PrivateJob job = await p.Initiate();
                await p.Authenticate(await p.Approve(job));
                if (missing == "Enabled") p.ModelOptions.Enabled = false;
                if (missing == "Direct") p.ModelOptions.DirectMcp.Enabled = false;
                if (missing == "Guide") p.ModelOptions.FullWorkIqGuide = false;
                await p.Worker.RunOne(default);
                Must(p.Model.Requests.Count == 0 && p.H.Handler.ToolCalls == 0 &&
                    !p.Store.Get(job.Id).HasRun && p.Store.Get(job.Id).State == PrivateJobState.AwaitingApproval &&
                    p.Store.Get(job.Id).Failure == NaturalLanguageOptions.WorkIqActivation);
            });
        await check("WorkIQ full-guide native failure preserves no-retry behavior", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            ModelHandler model = new(Completion("Unavailable")) { Status = HttpStatusCode.BadRequest };
            string result = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Please read the channel"), default))!;
            Must(result.Contains("400") && model.Requests.Count == 1 && h.Handler.ToolCalls == 0, result);
        });
        await check("Direct model may clarify without any fixed operation sequence or identity fallback", async () =>
        {
            await using Harness h = new(); DirectEnable(h);
            const string question = "Which of the two named files do you mean?";
            ModelHandler model = new(Completion(question));
            string result = (await App(h, model, DirectOptions()).Handle(Harness.Activity("Read one of those files"), default))!;
            Must(result.Contains(question) && model.Requests.Count == 1 && h.Handler.ToolCalls == 0, result);
            FullGuideRequests(model, WorkIqGuideMode.Direct);
        });
    }
}
