using System.ClientModel;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Apps.Schema.Entities;
using ModelContextProtocol.Protocol;
using OpenAI;
using OpenAI.Chat;

namespace WorkIqNaturalLanguage;

// Who asked, where, and what they said - read from the Teams activity.
internal sealed record Request(string Text, string RequesterId, string RequesterName, string ConversationId,
    string ConversationType, string? TeamId, string? ChannelId, string? MessageId, bool Overheard)
{
    internal bool IsPersonal => ConversationType == "personal";
}

// What the agent user did for a group/channel request, kept so the same person can confirm it in personal chat.
internal sealed record Handoff(Request Origin, string Answer, List<string> ToolCalls, DateTimeOffset At);

internal sealed class Agent(IConfiguration config, WorkIqConnection workIq)
{
    private const int MaxTurns = 16, MaxHistory = 10;
    private const string NoReply = "NO_REPLY";
    private static readonly string Skill = LoadSkill();
    private readonly ChatClient model = CreateModel(config);
    private readonly ConcurrentDictionary<string, List<(string User, string Answer)>> history = new();
    private readonly ConcurrentDictionary<string, Handoff> handoffs = new();

    // Personal chat and @mentions are addressed to the agent user. Other group chat / channel messages arrive only
    // when ambient listening is turned on for the blueprint (README); they are marked "overheard".
    internal static Request? Admit(MessageActivity activity, IConfiguration config)
    {
        JsonElement json = JsonSerializer.SerializeToElement(activity, JsonSerializerOptions.Web);
        static string? Field(JsonElement e, params string[] path)
        {
            foreach (string name in path)
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return null;
            return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        static bool Same(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        static Request? Drop(string why) { Console.WriteLine($"[admit] dropped: {why}"); return null; }
        string? au = config["WorkIQAgent:AgentUserObjectId"], sender = activity.From?.AadObjectId;
        string type = Field(json, "conversation", "conversationType") ?? "personal";
        bool mentioned = activity.GetMentions().Any(m => m.Mentioned?.Id == activity.Recipient?.Id);
        string raw = activity.Text ?? "", id = activity.Conversation?.Id ?? "";
        Console.WriteLine($"[in] message type={type} conv={id[..Math.Min(id.Length, 24)]} mentioned={mentioned} " +
            $"recipient={activity.Recipient?.Id}/{activity.Recipient?.AgenticUserId}/{activity.Recipient?.AadObjectId} text=\"{raw[..Math.Min(raw.Length, 60)]}\"");
        if (!Same(Field(json, "channelData", "tenant", "id"), config["AzureAd:TenantId"])) return Drop("tenant");
        if (!Same(activity.Recipient?.AgenticUserId ?? activity.Recipient?.AadObjectId, au)) return Drop("recipient is not Otto");
        if (string.IsNullOrEmpty(sender) || Same(sender, au)) return Drop("sender missing or is Otto");
        string text = (activity.TextWithoutMentions ?? "").Trim();
        if (text.Length == 0) return Drop("empty text");
        if (activity.Conversation?.Id is not string conversation) return Drop("no conversation id");
        bool overheard = type != "personal" && !mentioned;
        return new(text, sender, activity.From!.Name ?? "", conversation, type, Field(json, "channelData", "team", "aadGroupId"),
            Field(json, "channelData", "channel", "id"), activity.Id, overheard);
    }

    // Returns null when nothing should be posted (an overheard message Otto chose not to act on).
    internal async Task<string?> Handle(Request request, CancellationToken ct)
    {
        try { return await Run(request, ct); }
        catch (Exception error)
        {
            if (!request.Overheard) return "Sorry, something went wrong: " + error.Message;
            Console.WriteLine("Overheard message failed: " + error.Message);
            return null;
        }
    }

    private async Task<string?> Run(Request request, CancellationToken ct)
    {
        ChatCompletionOptions options = new();
        foreach (Tool tool in await workIq.Catalog(ct))
            options.Tools.Add(ChatTool.CreateFunctionTool(tool.Name, tool.Description ?? "", BinaryData.FromString(tool.InputSchema.GetRawText())));
        if (config["Model:ReasoningEffort"] is { Length: > 0 } effort)
        {
#pragma warning disable OPENAI001
            options.ReasoningEffortLevel = new ChatReasoningEffortLevel(effort);
#pragma warning restore OPENAI001
        }

        Handoff? handoff = request.IsPersonal ? handoffs.GetValueOrDefault(request.RequesterId) : null;
        List<(string User, string Answer)> turns = history.GetOrAdd(request.ConversationId, _ => []);
        List<ChatMessage> messages = [new SystemChatMessage(Skill + "\n\n" + Context(request, handoff))];
        lock (turns)
            foreach ((string user, string answer) in turns)
                messages.AddRange([new UserChatMessage(user), new AssistantChatMessage(answer)]);
        string userText = request.Overheard ? $"[overheard] {request.RequesterName}: {request.Text}" : request.Text;
        messages.Add(new UserChatMessage(userText));

        List<string> calls = [];
        for (int turn = 0; turn < MaxTurns; turn++)
        {
            ChatCompletion completion = (await model.CompleteChatAsync(messages, options, ct)).Value;
            if (completion.ToolCalls.Count == 0)
            {
                string answer = string.Join("\n", completion.Content.Select(c => c.Text)).Trim();
                lock (turns) { turns.Add((userText, answer)); if (turns.Count > MaxHistory) turns.RemoveAt(0); }
                if (!request.IsPersonal && !request.Overheard) handoffs[request.RequesterId] = new(request, answer, calls, DateTimeOffset.UtcNow);
                else if (handoff is not null) lock (handoff.ToolCalls) handoff.ToolCalls.AddRange(calls.Select(c => "[done in personal chat] " + c));
                if (request.Overheard && (answer.Length == 0 || answer == NoReply)) { Console.WriteLine("[agent] Overheard → NO_REPLY"); return null; }
                return answer.Length > 0 ? answer : "(The model returned an empty answer.)";
            }
            messages.Add(new AssistantChatMessage(completion));
            foreach (ChatToolCall call in completion.ToolCalls)
            {
                calls.Add(call.FunctionName + " " + call.FunctionArguments);
                using JsonDocument arguments = JsonDocument.Parse(call.FunctionArguments.ToString());
                messages.Add(new ToolChatMessage(call.Id, Text(await workIq.Call(call.FunctionName, arguments.RootElement, ct))));
            }
        }
        return $"Sorry, I stopped after {MaxTurns} steps without a final answer.";
    }

    private static string Context(Request r, Handoff? handoff)
    {
        StringBuilder text = new("## This request (facts from Teams, trusted)\n");
        text.AppendLine($"- Time (UTC): {DateTimeOffset.UtcNow:u}");
        text.AppendLine($"- Requester: {r.RequesterName} (userId {r.RequesterId}). You are the agent user; the requester is a different person.");
        text.AppendLine($"- Conversation type: {r.ConversationType}; conversation id: {r.ConversationId}");
        if (r.TeamId is not null) text.AppendLine($"- Team id: {r.TeamId}; channel id: {r.ChannelId}");
        if (r.MessageId is not null) text.AppendLine($"- Current message id: {r.MessageId}");
        if (r.Overheard)
            text.AppendLine($"- OVERHEARD: this message was NOT addressed to you (no @mention). Follow the skill section 'Overheard messages'; reply exactly {NoReply} to stay silent.");
        text.AppendLine("- Your final answer is posted back into this same conversation by the host.");
        if (handoff is not null)
        {
            Request o = handoff.Origin;
            text.AppendLine("\n## Earlier request from this same person in a group conversation (from host memory)");
            text.AppendLine($"- When (UTC): {handoff.At:u}; where: {o.ConversationType} {o.ConversationId}; message id {o.MessageId}");
            text.AppendLine($"- Their request: {o.Text}");
            text.AppendLine("- WorkIQ tool calls already made for it:" + (handoff.ToolCalls.Count == 0 ? " none" : ""));
            lock (handoff.ToolCalls) foreach (string call in handoff.ToolCalls) text.AppendLine("  - " + call);
            text.AppendLine($"- Your answer there:\n{handoff.Answer}");
        }
        return text.ToString();
    }

    // Native result as returned: structured content plus the text of each content block.
    private static string Text(CallToolResult result)
    {
        StringBuilder text = new();
        if (result.IsError == true) text.AppendLine("[WorkIQ reported isError=true]");
        if (result.StructuredContent is { } structured) text.AppendLine(structured.ToString());
        foreach (ContentBlock block in result.Content ?? [])
            text.AppendLine(block switch
            {
                TextContentBlock t => t.Text,
                EmbeddedResourceBlock { Resource: TextResourceContents r } => r.Text,
                ImageContentBlock => "[image returned; not shown to the model in this lab]",
                _ => JsonSerializer.Serialize(block),
            });
        return text.Length == 0 ? "(empty result)" : text.ToString();
    }

    private static ChatClient CreateModel(IConfiguration config) =>
        new(config["Model:Deployment"]!, new ApiKeyCredential(config["Model:ApiKey"]!),
            new OpenAIClientOptions { Endpoint = new Uri(config["Model:Endpoint"]!), NetworkTimeout = TimeSpan.FromMinutes(3) });

    private static string LoadSkill()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WorkIqNaturalLanguage.SKILL.md")!;
        return new StreamReader(stream).ReadToEnd();
    }
}
