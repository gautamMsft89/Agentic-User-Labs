using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;
using OpenAI.Chat;

namespace AzureOpenAiStreamingApp;

internal sealed class OpenAiAgenticOrchestrator : IAgentOrchestrator
{
    private readonly IChatClient _chatClient;
    private readonly McpClient _mcpClient;
    private readonly IList<AITool> _tools;
    private readonly AsyncLocal<ChannelWriter<AgentEvent>?> _updateWriter;

    private OpenAiAgenticOrchestrator(
        IChatClient chatClient,
        McpClient mcpClient,
        IList<AITool> tools,
        AsyncLocal<ChannelWriter<AgentEvent>?> updateWriter)
    {
        _chatClient = chatClient;
        _mcpClient = mcpClient;
        _tools = tools;
        _updateWriter = updateWriter;
    }

    internal static async Task<OpenAiAgenticOrchestrator> CreateAsync(
        IConfiguration configuration)
    {
        string model = RequiredConfiguration(configuration, "MODEL");
        string baseUrl = RequiredConfiguration(configuration, "AZURE_OPENAI_BASE_URL");
        string apiKey = RequiredConfiguration(configuration, "AZURE_OPENAI_API_KEY");

        McpClient mcpClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "MSLearn",
                Endpoint = new Uri("https://learn.microsoft.com/api/mcp")
            }));


        IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync();
        AsyncLocal<ChannelWriter<AgentEvent>?> updateWriter = new();

        IChatClient chatClient = new ChatClient(
            model,
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation(configure: functionInvokingClient =>
            {
                functionInvokingClient.FunctionInvoker = async (
                    context,
                    cancellationToken) =>
                {
                    if (updateWriter.Value is { } writer)
                    {
                        await writer.WriteAsync(
                            new AgentEvent(
                                $"Calling {context.Function.Name}",
                                IsInformative: true),
                            cancellationToken);
                    }

                    return await context.Function.InvokeAsync(
                        context.Arguments,
                        cancellationToken);
                };
            })
            .Build();

        return new OpenAiAgenticOrchestrator(
            chatClient,
            mcpClient,
            [.. mcpTools],
            updateWriter);
    }

    public async IAsyncEnumerable<IAgentEvent> GetUpdatesAsync(
        Microsoft.Teams.Apps.Schema.MessageActivity activity,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<AgentEvent> updates = Channel.CreateUnbounded<AgentEvent>();
        Task producer = ProduceUpdatesAsync(
            activity.Text ?? string.Empty,
            updates.Writer,
            cancellationToken);

        await foreach (AgentEvent update in updates.Reader.ReadAllAsync(
            cancellationToken))
        {
            yield return update;
        }

        await producer;
    }

    private async Task ProduceUpdatesAsync(
        string prompt,
        ChannelWriter<AgentEvent> writer,
        CancellationToken cancellationToken)
    {
        ChatOptions options = new()
        {
            Instructions = "You are a helpful assistant in Microsoft Teams.",
            Tools = _tools
        };

        ChannelWriter<AgentEvent>? previousWriter = _updateWriter.Value;
        _updateWriter.Value = writer;

        try
        {
            await writer.WriteAsync(
                new AgentEvent(
                    "Thinking...",
                    IsInformative: true),
                cancellationToken);  

            await foreach (ChatResponseUpdate response in
                _chatClient.GetStreamingResponseAsync(
                    new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, prompt),
                    options,
                    cancellationToken))
            {
                await writer.WriteAsync(
                    new AgentEvent(response.Text),
                    cancellationToken);
            }

            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
        finally
        {
            _updateWriter.Value = previousWriter;
        }
    }

    private static string RequiredConfiguration(
        IConfiguration configuration,
        string name) =>
        configuration[name] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Missing required configuration value: {name}");
}