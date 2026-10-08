namespace WorkIqFiles;

internal sealed record WorkIqHostPolicy(bool AllowHumanIdentity, bool HumanOAuthEnabled,
    bool PrivateJobsEnabled, string Principal, bool PrincipalConfigured)
{
    internal const string OptInKey = "WorkIQ:AllowHumanIdentity";
    internal const string HumanUnavailable = "Human WorkIQ identity is unavailable: WorkIQ:AllowHumanIdentity must be explicitly true, " +
        "with HumanWorkIQ:Enabled and its approved OAuth configuration. No human sign-in, private handoff or job execution was started. " +
        "The configured AU is not the human requester; no identity fallback is permitted.";
    internal string Status => $"WorkIQ data provider; effective principal={Principal}; principal source=" +
        (PrincipalConfigured ? "explicit configuration" : "AgentUser default") +
        $"; human identity opt-in={AllowHumanIdentity}; human OAuth={HumanOAuthEnabled}; private jobs={PrivateJobsEnabled}. " +
        "Retired WorkIQ recipe/setup flags are ignored and inactive; audience ACLs and native activation still apply.";
    internal string IdentityGuidance => Status + "\n" +
        (AllowHumanIdentity ? "Human capability requires its separately configured profile or approved private job; availability is not consent." :
            "Human identity is not enabled in this host. If the request requires authenticating as the human, explain that capability is unavailable; " +
            "do not offer sign-in or private handoff, make a data call on behalf of that human, or silently substitute AU credentials. " +
            "An explicit AU caller may access human-owned targets through AU permissions, but /me always means the AU, never the requester.") +
        " Respect an explicit caller choice; a conflict with the fixed profile is unavailable, not permission to change identity.";

    internal static WorkIqHostPolicy Read(IConfiguration configuration, NaturalLanguageOptions options)
    {
        // Binding preserves normal provider precedence and the existing AgentUser default only when absent.
        bool allow = configuration.GetValue<bool>(OptInKey);
        bool oauth = allow && configuration.GetValue<bool>("HumanWorkIQ:Enabled");
        bool jobs = oauth && configuration.GetValue<bool>("PrivateAnalysis:Enabled");
        string principal = options.DirectMcp.Principal;
        if (principal is not ("AgentUser" or "SignedInHuman"))
            throw new InvalidOperationException("Invalid NaturalLanguage:DirectMcp:Principal. Use AgentUser or SignedInHuman; no fallback.");
        if (principal == "SignedInHuman" && (!allow || !oauth))
            throw new InvalidOperationException("SignedInHuman explicitly selected but human identity is unavailable. Set WorkIQ:AllowHumanIdentity=true " +
                "and HumanWorkIQ:Enabled=true with approved OAuth settings, or explicitly select AgentUser yourself. No identity fallback.");
        return new(allow, oauth, jobs, principal, configuration.GetSection("NaturalLanguage:DirectMcp:Principal").Exists());
    }

    internal void RegisterHuman(IServiceCollection services, IConfiguration configuration, HumanSettings settings)
    {
        if (HumanOAuthEnabled) BrowserSignIn.Register(services, configuration, settings);
    }
    internal void RegisterPrivate(IServiceCollection services, PrivateAnalysisOptions options, LabSettings settings)
    {
        options.Enabled = PrivateJobsEnabled;
        if (PrivateJobsEnabled) PrivateAnalysisRegistration.Register(services, options, settings);
    }
    internal void MapHuman(IEndpointRouteBuilder app)
    {
        if (HumanOAuthEnabled) BrowserSignIn.Map(app);
    }
}
