using System.Diagnostics;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Handlers;

namespace WorkIqFiles;

internal static class PrivateCardInvoke
{
    internal static void Register(TeamsBotApplication teams, Func<PrivateAnalysisCoordinator> coordinator,
        CancellationToken host, ILogger logger) =>
        teams.OnInvoke((context, ct) => Handle(context.Activity, coordinator, host, ct, logger));

    internal static async Task<InvokeResponse> Handle(InvokeActivity activity, Func<PrivateAnalysisCoordinator> coordinator,
        CancellationToken host, CancellationToken incoming, ILogger logger)
    {
        using PrivateIngressDiagnostic diagnostic = new();
        Stopwatch watch = Stopwatch.StartNew();
        string action = "Other";
        if (activity.Name == InvokeNames.AdaptiveCardAction &&
            activity.Value is System.Text.Json.Nodes.JsonObject value &&
            value["action"] is System.Text.Json.Nodes.JsonObject command &&
            command["verb"] is System.Text.Json.Nodes.JsonValue verb &&
            verb.TryGetValue<string>(out string? text))
            action = text switch { "lab13.private.approve" => "Approve", "lab13.private.cancel" => "Cancel", _ => "Other" };
        logger.LogWarning("Private card invoke: correlation={Correlation}; event=Arrived; action={Action}. No raw details.",
            diagnostic.Correlation, action);
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(incoming, host);
        request.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            PrivateActionResult result = await coordinator().Action(activity, request.Token);
            logger.LogWarning("Private card invoke: correlation={Correlation}; event=ResponsePrepared; action={Action}; http=200; inner=200; elapsedMs={ElapsedMs}; boundCallbackGroupFlagTrusted={BoundCallbackGroupFlagTrusted}. No raw details.",
                diagnostic.Correlation, action, watch.ElapsedMilliseconds, diagnostic.BoundCallbackGroupFlagTrusted);
            return InvokeResponse.Ok(new AdaptiveCardResponse
            {
                StatusCode = 200, Type = result.Card is null ? AdaptiveCardResponseTypes.Message : AdaptiveCardResponseTypes.Card,
                Value = result.Card is { } card ? (object)card : result.Message
            });
        }
        catch (Exception error) when (PrivateFailures.SafeFailure(error))
        {
            logger.LogWarning("Private action denied/stopped; category={Category}; details withheld.", PrivateFailures.Category(error));
            diagnostic.Log(logger, error, routeFallback: false);
            logger.LogWarning("Private card invoke: correlation={Correlation}; event=ResponsePrepared; action={Action}; http=200; inner=400; elapsedMs={ElapsedMs}; boundCallbackGroupFlagTrusted={BoundCallbackGroupFlagTrusted}. No raw details.",
                diagnostic.Correlation, action, watch.ElapsedMilliseconds, diagnostic.BoundCallbackGroupFlagTrusted);
            // Handled Action.Execute failures use HTTP 200 with a typed inner error, not an HTTP transport error.
            return InvokeResponse.Ok(new AdaptiveCardResponse
            {
                StatusCode = 400, Type = "application/vnd.microsoft.error",
                Value = new { code = "BadRequest", message = "Private action unavailable, expired, busy or mismatched. Approval may already be recorded; analysis is never replayed. Use the valid private card or /private-jobs." }
            });
        }
    }
}
