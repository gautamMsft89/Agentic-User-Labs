using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkIqFiles;
using static WorkIqFiles.FileCommand;

internal static class SectionTwoChecks
{
    private static readonly string TeamPath = "/teams/" + Harness.Team;
    private static readonly string ChannelPath = TeamPath + "/channels/" + E(Harness.Channel);
    private static readonly string RootPath = ChannelPath + "/messages/100";
    private static readonly string PersonalPath = "/chats/19%3Apersonal/messages/300";
    internal static string Link(string message, string? parent = null, string context = Harness.Channel, string host = "teams.microsoft.com") =>
        $"https://{host}/l/message/{Uri.EscapeDataString(context)}/{message}?tenantId={Harness.Tenant}" +
        (context == Harness.Channel ? "&groupId=" + Harness.Team : "") +
        (parent is null ? "" : "&parentMessageId=" + parent);
    private static string Quoted(string value) => "\"" + value + "\"";
    private static void Must(bool condition, string message = "")
    { if (!condition) throw new Exception("Section2 assertion failed. " + message); }
    private static JsonObject Page(params JsonObject[] items) => new() { ["value"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()) };
    private static JsonObject Member(string user) => new()
    {
        ["id"] = "MEMBERSHIP-ID-NOT-USER", ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
        ["userId"] = user, ["tenantId"] = Harness.Tenant, ["displayName"] = user == Harness.Human ? "Requester" : "OTHER-NAME-NOT-DISPLAYED",
        ["roles"] = new JsonArray("owner")
    };
    private static JsonObject Message(string id, string? parent = null, bool personal = false) => new()
    {
        ["id"] = id, ["replyToId"] = parent, ["messageType"] = "message",
        ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = "<p>Hello <at>everyone</at> @tag</p>" },
        ["createdDateTime"] = "2026-09-25T19:20:00Z",
        ["from"] = new JsonObject { ["user"] = new JsonObject { ["id"] = Harness.Other, ["displayName"] = "Synthetic author" } },
        ["channelIdentity"] = personal ? null : new JsonObject { ["teamId"] = Harness.Team, ["channelId"] = Harness.Channel },
        ["chatId"] = personal ? "19:personal" : null,
        ["attachments"] = new JsonArray(new JsonObject
        {
            ["id"] = "attachment", ["name"] = "sample.txt", ["contentType"] = "reference",
            ["contentUrl"] = "https://private.sharepoint.com/path?sig=PRIVATE-QUERY",
            ["thumbnailUrl"] = "https://private.invalid/PRIVATE-THUMB", ["content"] = "PRIVATE-ATTACHMENT-CONTENT"
        })
    };
    internal static void Enable(Harness h)
    {
        h.AutoConnect = false;
        h.Policy.Contexts[0].SectionTwoEnabled = true;
        h.Policy.Contexts[2].SectionTwoEnabled = true;
        h.Policy.Validate();
        h.Handler.SectionTwoResults[TeamPath] = new() { ["id"] = Harness.Team, ["displayName"] = "Synthetic team", ["description"] = "Team info" };
        h.Handler.SectionTwoResults[ChannelPath] = new() { ["id"] = Harness.Channel, ["displayName"] = "Synthetic channel", ["membershipType"] = "standard" };
        h.Handler.SectionTwoResults[TeamPath + "/channels"] = Page(
            new() { ["id"] = Harness.Channel, ["displayName"] = "Standard name", ["membershipType"] = "standard" },
            new() { ["id"] = "private-channel", ["displayName"] = "PRIVATE-CHANNEL-NAME", ["membershipType"] = "private" },
            new() { ["id"] = "shared-channel", ["displayName"] = "SHARED-CHANNEL-NAME", ["membershipType"] = "shared" },
            new() { ["id"] = "unknown-channel", ["displayName"] = "UNKNOWN-CHANNEL-NAME", ["membershipType"] = "future" });
        h.Handler.SectionTwoResults[TeamPath + "/members"] = Page(Member(Harness.Human), Member(Harness.Other));
        h.Handler.SectionTwoResults[ChannelPath + "/allMembers"] = Page(Member(Harness.Human), Member(Harness.Au));
        h.Handler.SectionTwoResults[TeamPath + "/tags"] = Page(new JsonObject
        {
            ["id"] = "tag", ["teamId"] = Harness.Team, ["displayName"] = "<at>Tag</at> @everyone",
            ["description"] = "Only metadata", ["memberCount"] = 4, ["tagType"] = "standard"
        });
        h.Handler.SectionTwoResults[ChannelPath + "/messages"] = Page(Message("100"), Message("101"));
        h.Handler.SectionTwoResults[RootPath] = Message("100");
        h.Handler.SectionTwoResults[RootPath + "/replies"] = Page(Message("200", "100"));
        h.Handler.SectionTwoResults[RootPath + "/replies/200"] = Message("200", "100");
        h.Handler.SectionTwoResults[PersonalPath] = Message("300", personal: true);
    }
}
