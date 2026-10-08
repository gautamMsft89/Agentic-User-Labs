using Microsoft.Teams.Apps.Schema;
using System.Net;
using System.Text.RegularExpressions;

namespace WorkIqFiles;

internal sealed class WorkIqIngress(WorkIqRouter router, LabSettings settings, FilePolicy policy,
    IConversationAgent naturalLanguage)
{
    internal const string Retired = "Scripted slash data commands and their /confirm or /cancel tokens have been retired. No model or WorkIQ call was made. Describe the operation in ordinary language; native writes can execute immediately. Private jobs use /private-jobs and their own approval/cancellation controls.";
    internal const string Help = """
        WorkIQ LLM-native mode. Describe file or message work in ordinary language.
        The model selects native tools, targets, arguments and optional skill references.
        Native writes execute immediately when enabled; uncertain actions are never automatically replayed.
        /help /status /signin /disconnect
        Private jobs: /private-jobs and the existing private approval/cancellation controls.
        Sign-in and disconnect require the approved matched-human personal context.
        Human controls additionally require WorkIQ:AllowHumanIdentity=true and configured human OAuth.
        Private jobs also require PrivateAnalysis:Enabled=true; saved flags alone do not activate human identity.
        Scripted slash data commands, setup recipes and slash-write confirmation tokens are retired.
        """;

    internal static bool IsOperationalOrSlash(MessageActivity activity)
    {
        string text = ControlText(activity);
        return text.StartsWith('/') || text is "help" or "status";
    }
    internal static string ControlText(MessageActivity activity) =>
        WebUtility.HtmlDecode(Regex.Replace(activity.TextWithoutMentions ?? "", "<[^>]*>", "",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100))).Trim();

    internal async Task<string?> Handle(MessageActivity activity, CancellationToken ct, IDirectProgress? progress = null)
    {
        string text = ControlText(activity);
        if (!IsOperationalOrSlash(activity)) return await naturalLanguage.Handle(activity, ct, progress);
        try
        {
            Invocation invocation = policy.Authorize(activity, settings);
            return text switch
            {
                "/help" or "help" => Help + "\n" + router.HostStatus,
                "/status" or "status" => naturalLanguage.Status + "\n" + await router.HumanStatus(invocation, ct),
                "/signin" => await router.SignIn(invocation, ct),
                "/disconnect" => await router.Disconnect(invocation),
                "/private-jobs" => "Private jobs are inactive in this host; no stored job was accessed or executed. " + router.HostStatus,
                _ => Retired
            };
        }
        catch (LabException error) { return error.Message; }
    }
}
