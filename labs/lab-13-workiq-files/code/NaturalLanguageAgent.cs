using Microsoft.Teams.Apps.Schema;

namespace WorkIqFiles;

internal sealed class NaturalLanguageAgent(NaturalLanguageOptions options, WorkIqRouter router, FilePolicy policy,
    LabSettings settings, SetupMode setup, ChannelSetup channelSetup,
    Func<NaturalLanguageOptions, NaturalLanguageModel>? modelFactory = null, TimeProvider? timeProvider = null) : IConversationAgent
{
    private readonly DirectMcpAgent direct = new(options, router, policy, settings, setup, channelSetup, modelFactory, timeProvider);
    internal bool TeamsSelected => false;
    internal bool Enabled => options.Enabled;
    internal string Status =>
        $"Natural language enabled={options.Enabled}; DirectMcp enabled={options.DirectMcp.Enabled}; progressive WorkIQ skill={options.FullWorkIqGuide}; " +
        $"fixed {options.DirectMcp.Principal}. Native writes execute immediately without per-tool confirmation when activated. No guarded fallback.\n" +
        router.HostStatus;
    internal Task<string> HandleDirectCommand(MessageActivity activity, CancellationToken ct) =>
        direct.Handle(activity, ct);
    internal async Task<string?> Handle(MessageActivity activity, CancellationToken incoming, IDirectProgress? directProgress = null)
    {
        if (options.DirectMcp.UseTeamsMcp)
            throw new LabException("Teams MCP moved to lab-14-teams-mcp; Graph SDK moved to lab-15-graph-sdk. Lab13 cannot switch providers.");
        if (!options.Enabled || !options.DirectMcp.Enabled || !options.FullWorkIqGuide)
            return NaturalLanguageOptions.WorkIqActivation;
        return await direct.Handle(activity, incoming, directProgress);
    }
    bool IConversationAgent.TeamsSelected => TeamsSelected;
    bool IConversationAgent.Enabled => Enabled;
    string IConversationAgent.Status => Status;
    Task<string> IConversationAgent.HandleDirectCommand(MessageActivity activity, CancellationToken ct) => HandleDirectCommand(activity, ct);
    Task<string?> IConversationAgent.Handle(MessageActivity activity, CancellationToken ct, IDirectProgress? progress) => Handle(activity, ct, progress);
}
