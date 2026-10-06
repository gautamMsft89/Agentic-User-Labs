namespace AzureOpenAiStreamingApp;

internal interface IAgentEvent
{
    string Text { get; }
    bool IsInformative { get; }
}

internal sealed record AgentEvent(
    string Text,
    bool IsInformative = false) : IAgentEvent;

internal interface IAgentOrchestrator
{
    IAsyncEnumerable<IAgentEvent> GetUpdatesAsync(
        Microsoft.Teams.Apps.Schema.MessageActivity activity,
        CancellationToken cancellationToken);
}