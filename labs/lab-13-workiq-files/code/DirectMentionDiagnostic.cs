using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkIqFiles;

internal static class DirectMentionDiagnostic
{
    internal static string Describe(string tool, JsonElement args)
    {
        if (tool != "create_entity" || !args.TryGetProperty("parentUrl", out JsonElement parent) ||
            parent.ValueKind != JsonValueKind.String ||
            !Regex.IsMatch(parent.GetString()!, @"\A/(?:teams/[^/?]+/channels/[^/?]+|(?:me/)?chats/[^/?]+)/messages\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)) ||
            !args.TryGetProperty("jsonBody", out JsonElement bodyText) || bodyText.ValueKind != JsonValueKind.String) return "";
        using JsonDocument doc = JsonDocument.Parse(bodyText.GetString()!, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement body = doc.RootElement;
        static JsonElement Field(JsonElement obj, string name) =>
            obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement value) ? value : default;
        if (Field(body, "mentions") is not { ValueKind: JsonValueKind.Array } mentions) return "";
        JsonElement item = Field(body, "body"), content = Field(item, "content");
        string html = content.ValueKind == JsonValueKind.String ? content.GetString()! : "";
        MatchCollection matches = Regex.Matches(html, """<at\s+id\s*=\s*["']([0-9]{1,9})["']\s*>""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
        List<string> facts = [];
        foreach (JsonElement mention in mentions.EnumerateArray().Take(8))
        {
            JsonElement index = Field(mention, "id"), mentioned = Field(mention, "mentioned"), tag = Field(mentioned, "tag"), id = Field(tag, "id");
            JsonElement user = Field(mentioned, "user"), userId = Field(user, "id"), userType = Field(user, "userIdentityType");
            bool numeric = index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out _);
            bool marker = id.ValueKind == JsonValueKind.String &&
                Regex.IsMatch(id.GetString()!, @"\[(?:opaque value redacted|redacted|long opaque/binary value withheld)\]",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            string kind = tag.ValueKind == JsonValueKind.Object ? "Tag" :
                Field(mentioned, "user").ValueKind == JsonValueKind.Object ? "User" :
                Field(mentioned, "conversation").ValueKind == JsonValueKind.Object ? "Conversation" : "Unestablished";
            facts.Add($"mention[{facts.Count}] target={kind}; tagIdPresent={id.ValueKind == JsonValueKind.String && id.GetString()!.Length > 0}; " +
                $"tagIdRedactionMarker={marker}; numericIndex={numeric}; " +
                $"simpleHtmlIndexMatch={numeric && matches.Any(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == index.GetInt32())}; " +
                $"tagTypeSupplied={Field(tag, "@odata.type").ValueKind != JsonValueKind.Undefined}; " +
                $"userIdPresent={userId.ValueKind == JsonValueKind.String && userId.GetString()!.Length > 0}; " +
                $"userIdentityTypeSupplied={userType.ValueKind != JsonValueKind.Undefined}; " +
                $"userIdentityTypeAadUser={userType.ValueKind == JsonValueKind.String && userType.GetString() == "aadUser"}; " +
                $"userTypeAnnotationSupplied={Field(user, "@odata.type").ValueKind != JsonValueKind.Undefined}");
        }
        return $"; local mention structure: count={mentions.GetArrayLength()}; simpleHtmlAtCount={matches.Count}; " +
            $"htmlDeclared={Field(item, "contentType").ValueKind == JsonValueKind.String && Field(item, "contentType").GetString() == "html"}; " +
            $"messageTypeSupplied={Field(body, "@odata.type").ValueKind != JsonValueKind.Undefined}; " +
            $"bodyKind={item.ValueKind}; contentKind={content.ValueKind}; " +
            $"bodyTypeSupplied={Field(item, "@odata.type").ValueKind != JsonValueKind.Undefined}; " +
            string.Join("; ", facts) + "; lexical facts only, not service validation; detailLimit=8";
    }
}
