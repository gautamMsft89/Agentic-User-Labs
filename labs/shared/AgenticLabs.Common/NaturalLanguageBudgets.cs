namespace WorkIqFiles;

internal sealed class NaturalLanguageBudgetOptions
{
    public string Profile { get; set; } = "Generous";
    public int? RequestSeconds { get; set; }
    public int? McpCallSeconds { get; set; }
    public int? ModelTurns { get; set; }
    public int? ModelToolCalls { get; set; }
    public int? ToolsPerTurn { get; set; }
    public int? McpCalls { get; set; }
    public int? McpResponseBytes { get; set; }
    public int? ModelRequestBytes { get; set; }
    public int? ModelResponseBytes { get; set; }
    public int? ConversationChars { get; set; }
    public int? ToolResultChars { get; set; }
    public int? InputChars { get; set; }
    public int? ArgumentBytes { get; set; }
    public int? SchemaOutlineBytes { get; set; }
    public int? FinalAnswerChars { get; set; }
    public int? ReplyBytes { get; set; }
    internal NaturalLanguageBudgets Resolve(bool direct = false)
    {
        NaturalLanguageBudgets baseline = Profile switch
        {
            "Standard" => NaturalLanguageBudgets.Standard,
            "Generous" => direct ? NaturalLanguageBudgets.DirectGenerous : NaturalLanguageBudgets.Generous,
            _ => throw new LabException("NaturalLanguage:Budgets:Profile must be Standard or Generous.")
        };
        int Pick(string name, int? value, int fallback, int min, int max)
        {
            int result = value ?? fallback;
            if (result < min || result > max)
                throw new LabException($"Invalid NaturalLanguage:Budgets:{name}; allowed [{min},{max}], configured [{result}].");
            return result;
        }
        NaturalLanguageBudgets result = new(
            Pick(nameof(RequestSeconds), RequestSeconds, baseline.RequestSeconds, 1, 900),
            Pick(nameof(McpCallSeconds), McpCallSeconds, baseline.McpCallSeconds, 1, 600),
            Pick(nameof(ModelTurns), ModelTurns, baseline.ModelTurns, 1, 32),
            Pick(nameof(ModelToolCalls), ModelToolCalls, baseline.ModelToolCalls, 1, 64),
            Pick(nameof(ToolsPerTurn), ToolsPerTurn, baseline.ToolsPerTurn, 1, 16),
            Pick(nameof(McpCalls), McpCalls, baseline.McpCalls, 1, 512),
            Pick(nameof(McpResponseBytes), McpResponseBytes, baseline.McpResponseBytes, 16384, (direct ? 8 : 4) * 1024 * 1024),
            Pick(nameof(ModelRequestBytes), ModelRequestBytes, baseline.ModelRequestBytes, 16384, (direct ? 16 : 4) * 1024 * 1024),
            Pick(nameof(ModelResponseBytes), ModelResponseBytes, baseline.ModelResponseBytes, 16384, 2 * 1024 * 1024),
            Pick(nameof(ConversationChars), ConversationChars, baseline.ConversationChars, 4096, direct ? 2000000 : 1000000),
            Pick(nameof(ToolResultChars), ToolResultChars, baseline.ToolResultChars, 1024, direct ? 1000000 : 128000),
            Pick(nameof(InputChars), InputChars, baseline.InputChars, 1, 32000),
            Pick(nameof(ArgumentBytes), ArgumentBytes, baseline.ArgumentBytes, 256, 65536),
            Pick(nameof(SchemaOutlineBytes), SchemaOutlineBytes, baseline.SchemaOutlineBytes, 1024, 65536),
            Pick(nameof(FinalAnswerChars), FinalAnswerChars, baseline.FinalAnswerChars, 1, 20000),
            Pick(nameof(ReplyBytes), ReplyBytes, baseline.ReplyBytes, 4096, 24576));
        if (result.SchemaOutlineBytes > result.ToolResultChars)
            throw new LabException("SchemaOutlineBytes must not exceed ToolResultChars; schema outlines use ASCII JSON.");
        return result;
    }
}

internal sealed record NaturalLanguageBudgets(int RequestSeconds, int McpCallSeconds, int ModelTurns,
    int ModelToolCalls, int ToolsPerTurn, int McpCalls, int McpResponseBytes, int ModelRequestBytes,
    int ModelResponseBytes, int ConversationChars, int ToolResultChars, int InputChars, int ArgumentBytes,
    int SchemaOutlineBytes, int FinalAnswerChars, int ReplyBytes)
{
    internal static readonly NaturalLanguageBudgets Standard =
        new(120, 120, 8, 12, 4, 96, 262144, 262144, 262144, 60000, 14000, 4096, 8192, 8192, 6000, 24576);
    internal static readonly NaturalLanguageBudgets Generous =
        new(300, 180, 16, 32, 8, 256, 1048576, 2097152, 1048576, 256000, 32000, 16000, 32768, 24576, 12000, 24576);
    // Native schemas stay intact; allow a final model turn after longer native discovery sequences.
    internal static readonly NaturalLanguageBudgets DirectGenerous = Generous with
    {
        ModelTurns = 32,
        ToolResultChars = 512000, ConversationChars = 1000000,
        McpResponseBytes = 4 * 1024 * 1024, ModelRequestBytes = 8 * 1024 * 1024
    };
    internal static NaturalLanguageBudgets Current => OperationProgress.Current?.NaturalLanguageBudgets ?? Standard;
    internal static void Require(string name, long observed, long limit)
    {
        if (observed > limit) throw new LabException($"Natural-language limit [{name}]: observed [{observed}], limit [{limit}]. " +
            "No truncated data treated as complete; narrow the request or adjust the named budget within its supported range.");
    }
}
