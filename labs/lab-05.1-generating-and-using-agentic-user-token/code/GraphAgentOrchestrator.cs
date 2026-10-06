using System.Runtime.CompilerServices;

namespace GraphApiStreamingApp;

internal sealed class GraphAgentOrchestrator : IAgentOrchestrator
{
    private readonly GraphService _graphService;

    public GraphAgentOrchestrator(GraphService graphService) =>
        _graphService = graphService;

    public async IAsyncEnumerable<IAgentEvent> GetUpdatesAsync(
        Microsoft.Teams.Apps.Schema.MessageActivity activity,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new AgentEvent(
            "Fetching your profile from Microsoft Graph",
            IsInformative: true);

        string userId = activity.From?.AadObjectId ?? string.Empty;

        UserProfile profile = await _graphService.GetUserProfileAsync(
            userId,
            activity.Recipient?.GetAgenticIdentity(),
            cancellationToken);

        string message =
            $"Hi `{profile.DisplayName}`, Microsoft Graph returned your email as `{profile.Email}`.";

        foreach (string word in message.Split(' '))
        {
            await Task.Delay(TimeSpan.FromSeconds(0.1), cancellationToken);

            yield return new AgentEvent($"{word} ");
        }
    }
}
