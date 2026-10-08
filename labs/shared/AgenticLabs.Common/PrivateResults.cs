using System.Text.Json;
namespace WorkIqFiles;

internal sealed record PrivateIngress(bool Handled, string? Reply);
internal sealed record PrivateActionResult(string Message, JsonElement? Card = null);

