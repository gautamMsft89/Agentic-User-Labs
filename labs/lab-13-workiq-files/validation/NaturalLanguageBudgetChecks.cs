using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using WorkIqFiles;

internal static partial class NaturalLanguageChecks
{
    private static JsonObject TextEnvelope(string text) => new()
    { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = false };
    private static JsonObject DescribedSchema(string description) => new()
    {
        ["type"] = "object", ["description"] = description,
        ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } }
    };
    private static async Task BudgetAndSchemaChecks(Func<string, Func<Task>, Task> check)
    {
        await check("NL configuration binds profile and named overrides through application schema", () =>
        {
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NaturalLanguage:Budgets:Profile"] = "Generous",
                    ["NaturalLanguage:Budgets:ModelTurns"] = "20",
                    ["NaturalLanguage:Budgets:McpResponseBytes"] = "2097152"
                }).Build();
            NaturalLanguageOptions options = configuration.GetSection("NaturalLanguage").Get<NaturalLanguageOptions>()!;
            NaturalLanguageBudgets resolved = options.Budgets.Resolve();
            Must(!options.Enabled && resolved.ModelTurns == 20 && resolved.McpResponseBytes == 2097152 &&
                resolved.RequestSeconds == 300 && resolved.ModelToolCalls == 32);
            return Task.CompletedTask;
        });
        await check("NL Generous defaults are coherent finite and all numeric settings reject unbounded values", async () =>
        {
            NaturalLanguageBudgetOptions options = new();
            Must(options.Resolve() == NaturalLanguageBudgets.Generous);
            foreach (var property in typeof(NaturalLanguageBudgetOptions).GetProperties().Where(p => p.PropertyType == typeof(int?)))
            {
                NaturalLanguageBudgetOptions bad = new();
                property.SetValue(bad, -1);
                await Denied(() => { bad.Resolve(); return Task.CompletedTask; });
                property.SetValue(bad, int.MaxValue);
                await Denied(() => { bad.Resolve(); return Task.CompletedTask; });
            }
            await Denied(() => { new NaturalLanguageBudgetOptions { SchemaOutlineBytes = 65536 }.Resolve(); return Task.CompletedTask; });
        });
        await check("NL underlying MCP count above old cap is invocation-scoped", async () =>
        {
            await using Harness h = new(); Enable(h);
            using OperationProgress progress = new()
            { NaturalLanguageBudgets = NaturalLanguageBudgets.Generous with { McpCalls = 100 }, NaturalLanguageCallBudget = 100 };
            for (int i = 0; i < 100; i++) await h.AuSession.CallAsync("fetch", new() { ["entityUrls"] = new[] { Team } }, false, default);
            await Denied(async () => { await h.AuSession.CallAsync("fetch", new() { ["entityUrls"] = new[] { Team } }, false, default); });
            Must(h.Handler.ToolCalls == 100 && progress.CallsStarted == 100);
        });
        await check("NL model request uses relaxed profile above old byte cap", async () =>
        {
            NaturalLanguageOptions options = Options(); options.Budgets.Profile = "Generous";
            ModelHandler handler = new(Completion("Ready"));
            using NaturalLanguageModel model = new(options, handler);
            await model.Client.CompleteChatAsync([new OpenAI.Chat.UserChatMessage(new string('x', 300000))]);
            Must(handler.Requests.Count == 1);
        });
        await check("NL model response uses relaxed profile above old byte cap", async () =>
        {
            NaturalLanguageOptions options = Options(); options.Budgets.Profile = "Generous";
            ModelHandler handler = new(Completion(new string('x', 300000)));
            using NaturalLanguageModel model = new(options, handler);
            var reply = await model.Client.CompleteChatAsync([new OpenAI.Chat.UserChatMessage("Read")]);
            Must(reply.Value.Content[0].Text.Length == 300000);
        });
    }
}
