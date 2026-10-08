using System.Text.Json;
using WorkIqFiles;

internal static class NativeAuthFixture
{
    internal static async Task<string?> Read(WorkIqRouter router, FilePolicy policy, LabSettings settings,
        string drive, string item, string user = Harness.Human)
    {
        bool human = drive != "drive";
        string label = human ? "WorkIQ identity: requesting user" : "WorkIQ identity: AU";
        try
        {
            Invocation invocation = policy.Authorize(Harness.Activity("Synthetic native auth read", "personal", user), settings);
            await using RouteLease lease = await router.AcquireDirect(invocation, human ? "SignedInHuman" : "AgentUser", default);
            ToolReply reply = await lease.Backend.CallAsync("fetch",
                new() { ["entityUrls"] = new[] { FileCommand.ItemPath(drive, item) } }, false, default);
            return label + "\nname=" + JsonSerializer.Serialize(reply.Result);
        }
        catch (Exception error) when (PrivateFailures.SafeFailure(error)) { return label + "\n" + error.Message; }
    }
    internal static Task<string?> Read(Harness h, string drive, string item, string user = Harness.Human) =>
        Read(h.Router, h.Policy, h.Settings, drive, item, user);
    internal static Task<string?> Read(OidcFixture f, string drive, string item, string user = Harness.Human) =>
        Read(f.Services.GetRequiredService<WorkIqRouter>(), f.Services.GetRequiredService<FilePolicy>(),
            f.Services.GetRequiredService<LabSettings>(), drive, item, user);
}
