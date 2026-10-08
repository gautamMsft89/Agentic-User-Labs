using System.Text.Json;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Core;
using Microsoft.Teams.Core.Http;
using Microsoft.Teams.Core.Schema;

namespace WorkIqFiles;

internal static class PrivateCards
{
    internal static MessageActivity Status(PrivateJob job) => Card(new object[]
    {
        new { type = "TextBlock", text = "Private work is queued or running under " + job.ConsentScope + ". Cancellation stops future work; already dispatched operations may finish remotely and prior effects are not undone.", wrap = true }
    }, [new { type = "Action.Execute", title = "Cancel private work", verb = "lab13.private.cancel",
        data = new { job = job.Id, revision = job.Revision } }]);
    internal static MessageActivity Approval(PrivateJob job) => Card(new object[]
    {
        new { type = "TextBlock", text = job.HasRun ? "Recover private result delivery (no execution replay)" :
            job.ConsentScope == PrivateConsentScope.LegacyReadOnly ? "Authorize legacy read-only analysis" : "Authorize native WorkIQ work as you", weight = "Bolder", wrap = true },
        new { type = "TextBlock", text = job.Request, wrap = true },
        new { type = "TextBlock", text = ScopeNotice(job.ConsentScope) +
            " Results return to this established personal route; its current roster/type are not reverified. After approval, sign-in and starting work must occur within five minutes; the request expires within thirty minutes.", wrap = true }
    }, new object[]
    {
        new { type = "Action.Execute", title = job.HasRun ? "Authorize result recovery" :
            job.ConsentScope == PrivateConsentScope.LegacyReadOnly ? "Authorize legacy read-only analysis" : "Authorize read/write work as me",
            verb = "lab13.private.approve", data = new { job = job.Id, revision = job.Revision } },
        new { type = "Action.Execute", title = "Cancel", verb = "lab13.private.cancel", data = new { job = job.Id, revision = job.Revision } }
    });
    internal static string ScopeNotice(PrivateConsentScope scope) => scope == PrivateConsentScope.LegacyReadOnly
        ? "Scope LegacyReadOnly: read files only, no writes or sharing. This old request cannot be upgraded; submit a new request for native read/write work."
        : "Scope NativeReadWriteV1: native reads, writes and actions as your matching signed-in human. May change, delete, share or send. Selected operations execute immediately after this job approval, with no per-tool confirmation. WorkIQ enforces your permissions, not your intent.";
    internal static JsonElement SignIn(string url, PrivateConsentScope scope = PrivateConsentScope.LegacyReadOnly) => CardContent(new object[]
    {
        new { type = "TextBlock", text = "Sign in as the same human account you use in Teams. Only the separately approved request and scope can continue. " + ScopeNotice(scope), wrap = true }
    }, [new { type = "Action.OpenUrl", title = "Sign in for this private request", url }]);
    private static JsonElement CardContent(object[] body, object[] actions) =>
        JsonSerializer.SerializeToElement(new { type = "AdaptiveCard", version = "1.5", body, actions });
    private static MessageActivity Card(object[] body, object[] actions) => new MessageActivity().AddAttachment(
        TeamsAttachment.CreateBuilder().WithAdaptiveCard(CardContent(body, actions)).Build());
}
