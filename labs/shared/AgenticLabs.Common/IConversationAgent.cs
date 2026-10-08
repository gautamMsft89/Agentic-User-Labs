using Microsoft.Teams.Apps.Schema;

namespace WorkIqFiles;

internal interface IConversationAgent
{
    bool TeamsSelected { get; }
    bool Enabled { get; }
    string Status { get; }
    Task<string> HandleDirectCommand(MessageActivity activity, CancellationToken ct);
    Task<string?> Handle(MessageActivity activity, CancellationToken ct, IDirectProgress? progress = null);
}
