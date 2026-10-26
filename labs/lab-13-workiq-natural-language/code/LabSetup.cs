namespace WorkIqNaturalLanguage;

// Reads all settings from lab13.config.json and stops with a clear list when something is missing.
internal static class LabSetup
{
    internal const string ConfigFile = "lab13.config.json";

    private static readonly (string Section, string Key)[] Required =
    [
        ("Teams / agent user", "AzureAd:TenantId"),
        ("Teams / agent user", "AzureAd:ClientId"),
        ("Teams / agent user", "AzureAd:ClientCredentials:0:ClientSecret"),
        ("Teams / agent user", "WorkIQAgent:AgentIdentityClientId"),
        ("Teams / agent user", "WorkIQAgent:AgentUserObjectId"),
        ("WorkIQ", "WorkIQ:Endpoint"),
        ("WorkIQ", "WorkIQ:Scope"),
        ("Model", "Model:Endpoint"),
        ("Model", "Model:Deployment"),
        ("Model", "Model:ApiKey"),
    ];

    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["WorkIQ:Endpoint"] = "https://workiq.svc.cloud.microsoft/mcp",
        ["WorkIQ:Scope"] = "api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask",
        ["AzureAd:ClientCredentials:0:SourceType"] = "ClientSecret",
    };

    // Returns false when the lab cannot start.
    internal static bool Load(ConfigurationManager config, string folder)
    {
        string path = Path.GetFullPath(Path.Combine(folder, ConfigFile));
        if (!File.Exists(path))
        {
            Console.WriteLine($"No {ConfigFile} in {folder}. Create it from the template, then fill it in (README step 1):");
            Console.WriteLine("  Copy-Item lab13.config.example.json lab13.config.json");
            return false;
        }
        config.AddJsonFile(path, optional: false, reloadOnChange: false);
        if (string.IsNullOrEmpty(config["WorkIQ:Scope"]) && config["WorkIQ:Resource"] is { Length: > 0 } resource)
            config["WorkIQ:Scope"] = resource.TrimEnd('/') + "/WorkIQAgent.Ask";
        config.AddInMemoryCollection(Values(config, Defaults));

        var missing = Required.Where(r => config[r.Key] is not { Length: > 0 } v || v.Contains('<')).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine($"Cannot start: fill in these values in {path} (README step 1):");
            foreach (var section in missing.GroupBy(r => r.Section))
                Console.WriteLine($"  [{section.Key}] {string.Join(", ", section.Select(r => r.Key))}");
            return false;
        }

        // The Teams SDK reads AzureAd; the agent user token reads the blueprint from WorkIQAgent:Blueprint.
        config.AddInMemoryCollection(Values(config, new()
        {
            ["WorkIQAgent:TenantId"] = config["AzureAd:TenantId"]!,
            ["WorkIQAgent:Blueprint:ClientId"] = config["AzureAd:ClientId"]!,
            ["WorkIQAgent:Blueprint:TenantId"] = config["AzureAd:TenantId"]!,
            ["WorkIQAgent:Blueprint:Instance"] = "https://login.microsoftonline.com/",
            ["WorkIQAgent:Blueprint:ClientCredentials:0:SourceType"] = "ClientSecret",
            ["WorkIQAgent:Blueprint:ClientCredentials:0:ClientSecret"] = config["AzureAd:ClientCredentials:0:ClientSecret"]!,
        }));
        Console.WriteLine($"Settings loaded from {path}.");
        Console.WriteLine($"Teams messaging endpoint: {config["Host:TunnelUrl"]?.TrimEnd('/')}/api/messages (local {config["Urls"]})");
        return true;
    }

    // Only fills keys the file left empty.
    private static IEnumerable<KeyValuePair<string, string?>> Values(IConfiguration config, Dictionary<string, string> values) =>
        values.Where(v => string.IsNullOrEmpty(config[v.Key])).Select(v => KeyValuePair.Create(v.Key, (string?)v.Value));
}
