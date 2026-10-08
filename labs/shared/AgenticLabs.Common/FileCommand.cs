using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Teams.Apps.Schema;

namespace WorkIqFiles;

internal class LabException(string message) : Exception(message);
internal sealed class UnknownOutcomeException(bool requiresSignIn = false, string diagnostic = "") : LabException(
    "UNKNOWN OUTCOME: WorkIQ may have executed the mutation. Never automatically replay. Inspect the target before authorizing any new operation." +
    (diagnostic.Length == 0 ? "" : "\n" + diagnostic))
{
    internal bool RequiresSignIn => requiresSignIn;
}

internal static class FileCommand
{
    internal static string Id(string value)
    {
        if (value.Length is 0 or > 512 || !Regex.IsMatch(value, @"\A[A-Za-z0-9_:@.+!-]+\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) || value is "." or "..")
            throw new LabException("Expected a raw resource ID, not a URL/path/query.");
        return value;
    }
    internal static string Name(string value)
    {
        if (value.Length is 0 or > 100 || value != value.Trim() || value.EndsWith('.') ||
            value.Any(c => char.IsControl(c) || "\"*:<>?/\\|#%".Contains(c)) || value is "." or "..")
            throw new LabException("Invalid synthetic file/folder name (1-100 characters).");
        return value;
    }
    internal static string E(string id) => Uri.EscapeDataString(Id(id));
    internal static string ItemPath(string drive, string item) => $"/drives/{E(drive)}/items/{E(item)}";
    internal static string? Field(JsonElement element, params string[] path)
    {
        foreach (string name in path)
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
    internal static string Required(JsonElement element, params string[] path) =>
        Field(element, path) is { Length: > 0 } value ? value : throw new LabException("Required authoritative metadata missing.");
}
