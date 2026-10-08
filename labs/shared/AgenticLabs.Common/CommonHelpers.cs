using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenAI.Chat;

namespace WorkIqFiles;

internal static class BotText
{
    internal static string Html(string text) => "<pre>" + WebUtility.HtmlEncode(text) + "</pre>";
}
internal static class ContentHash
{
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
internal static class PrivateFailures
{
    internal static bool SafeFailure(Exception error) => error is LabException or HttpRequestException or OperationCanceledException or
        IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or System.ClientModel.ClientResultException or
        Microsoft.Identity.Client.MsalException or Microsoft.Identity.Web.MicrosoftIdentityWebChallengeUserException;
    internal static string Category(Exception error) => error is HttpRequestException ? "HTTP" :
        error is OperationCanceledException ? "cancelled" : "validation-or-provider";
}
internal static class ModelOptions
{
    internal static ChatCompletionOptions Options(string? effort)
    {
        ChatCompletionOptions options = new();
        if (!string.IsNullOrEmpty(effort))
        {
#pragma warning disable OPENAI001
            options.ReasoningEffortLevel = new ChatReasoningEffortLevel(effort);
#pragma warning restore OPENAI001
        }
        return options;
    }
}
