# Lab 13 - LLM-native WorkIQ

## Current implementation

**WorkIQ data operations are LLM-native only.** The old deterministic slash recipes,
preview/confirmation store, setup discovery, automatic named-file resolution and
guarded read-only broker have been removed, not hidden behind a flag.
`WorkIqIngress` handles operational controls and passes ordinary requests to
`NaturalLanguageAgent` / `DirectMcpAgent`. There is no data fallback.

| Input | Current behavior |
|---|---|
| Ordinary text, optionally with attachments | Fixed configured AU or matched-human native flow; model selects tools, targets, original arguments and optional skill references |
| `/help`, `/status` (also exact `help`, `status`) | Operational information; no model/catalog/data call |
| `/signin`, `/disconnect` | Matched-human connection lifecycle only with explicit human opt-in and configured OAuth; otherwise unavailable, with no credential access/deletion |
| `/private-jobs`, existing private approval/cancel cards | Authorized private-job lifecycle only when explicitly enabled; otherwise inactive, with no stored-job access or execution |
| Any other leading slash input | Explicit retired/unsupported guidance, zero model/catalog/data calls, including `/file`, `/folder`, `/upload-folder`, `/setup`, `/team`, `/channel`, `/message`, `/thread`, provider suffixes and `/confirm` / `/cancel` / `/mcp-*` |

The slash guard recognizes leading whitespace and HTML wrappers/entities, but never rewrites
the original text supplied to the model. It runs before private intent routing as well.
Approval of a private native job remains separate from obsolete slash-write tokens.
Natural-language actions can write immediately once the native profile is enabled: there is
no slash preview or per-tool confirmation. Do not use a former `EnableMutations=false` value
as a native-mode read-only switch; it was a scripted/Graph policy field, not a native tool gate.
Service permissions, WorkIQ governance, fixed-principal checks, schema validation, budgets,
private authorization and mutation no-replay protections remain.

## Configuration and launch

Prerequisites: .NET 10 SDK, the existing Teams AU/blueprint delivery registration and credentials,
the configured AU's WorkIQ access, and your Azure model endpoint/deployment/key.
Use `code\appsettings.example.json` only as a placeholder reference; do not overwrite existing
`code\appsettings.json`. The project path and user-secrets ID
`agentic-user-labs-13-workiq-files-isolated` are unchanged. Startup does not rewrite local settings
or provision credentials.

Enable `NaturalLanguage:Enabled`, `NaturalLanguage:DirectMcp:Enabled` and
`NaturalLanguage:FullWorkIqGuide`. Keep `UseTeamsMcp=false`; choose the intended fixed
`Principal` (`AgentUser` or `SignedInHuman`). **When absent, Principal defaults to AgentUser.
`WorkIQ:AllowHumanIdentity` defaults to false.** Saved `HumanWorkIQ:Enabled=true`,
`PrivateAnalysis:Enabled=true`, cached credentials and persisted jobs cannot opt you into human
mode. With opt-in off, no human OAuth scheme/start/callback, human acquisition, private coordinator,
card handler or worker is registered; dormant jobs/credentials are neither resumed nor deleted.
An explicit `SignedInHuman` conflicts with opt-in off (or `HumanWorkIQ:Enabled=false`) and fails
with actionable guidance; it is never converted to AU. Invalid/empty principals fail closed.
Normal configuration precedence applies, including explicit false values.
Missing native activation returns explicit configuration guidance without a guarded fallback.
No model subsystem is automatically enabled by these defaults.

Saved enabled `SetupMode`, `ChannelSetup`, scripted `SectionTwoEnabled`,
group-share/roster, upload-mapping, channel-create/rename/move/artifact/content-edit and
`AutoExecute*TestMode` flags are now **ignored**, not startup blockers. Only their effective
in-memory recipe activations become false, before policy validation and service registration.
A bounded startup warning lists at most fifteen fixed flag names (context indices/identifiers and
values are not logged); saved settings are not rewritten. These flags do not grant native consent.
Startup and `/help`/`/status` report the effective principal and human/private capability.
Audience tenant/requester/conversation ACLs and existing roots remain intact. A retired rootless
group audience may remain in Lab13 configuration, but group native data access, personal-context
reuse and human private handoff remain denied. Other providers retain their existing validation.
Shared policy models still retain some compatibility
fields for Graph and private policy fingerprints. `AutoResolveChannelFolder` in the sample
allows the existing empty-root channel policy shape; the native host performs **no automatic
folder lookup**. The model must select any required lookup itself.

Foreign-provider flags are **not** retired WorkIQ recipes: `NaturalLanguage:DirectMcp:UseTeamsMcp`,
enabled `TeamsMcp:GraphFiles/GraphChat/GraphChannel/HumanChat/PrivateGraph` and `GraphRscComparison`
still fail isolation checks. Resolve those deliberately for Lab13 or use the corresponding separate lab;
this change never silently changes a selected data provider.

From `labs\lab-13-workiq-files\code`:

```powershell
dotnet build .\WorkIqFiles.csproj -c Release --no-restore
dotnet run --project .\WorkIqFiles.csproj -c Release --no-build --no-launch-profile --urls http://localhost:3983
```

The original `dotnet run --project .\WorkIqFiles.csproj --no-launch-profile --urls http://localhost:3983`
still selects Debug and builds first. `--no-launch-profile` does not select Release or suppress
environment settings. Host/endpoint/tunnel activation remains operator-owned. No host was started.

To explicitly choose AU for this launch, overriding a saved human principal without editing settings:

```powershell
dotnet run --project .\WorkIqFiles.csproj -c Release --no-build --no-launch-profile --urls http://localhost:3983 --NaturalLanguage:DirectMcp:Principal=AgentUser --WorkIQ:AllowHumanIdentity=false
```

### Fresh-terminal AU setup

A new terminal does not inherit process-only settings from a previous terminal. Keep the existing
ignored `appsettings.json`: do **not** replace it with the sample. Preserve its approved audience,
AU identity and saved provider choices (`AgentUser`, human opt-in false, Teams MCP and Graph modes
disabled); see the isolation guidance above if a higher-priority setting selects another provider.
The activation and credential assignments below apply only to this terminal and its child process;
they are not automatically persisted.

Teams reply authentication and WorkIQ data authentication have separate credential bindings:

| Role | App identity to verify | Credential setting |
|---|---|---|
| Teams bot/AU transport replies | `AzureAd:ClientId` | `AzureAd:ClientCredentials:0:ClientSecret` |
| AU WorkIQ blueprint token acquisition | `WorkIQAgent:Blueprint:ClientId` | `WorkIQAgent:Blueprint:ClientCredentials:0:ClientSecret` |
| Azure model resource | Matching model endpoint and deployment | `NaturalLanguage:ApiKey` (not an Entra client secret) |

For Entra credentials, select the app by its **application/client ID** under **Entra ID > App
registrations > Certificates & secrets > Client secrets**. Use the secret **Value**, not its
Secret ID. An existing secret value cannot be redisplayed; retrieve it through your approved
secure source or provisioning process. The same secret is appropriate for both Entra roles
**only if both configured client IDs identify the same app**. Otherwise supply each app's
appropriate secret. Do not automatically copy credentials between roles.

Run this setup yourself in PowerShell. Enter actual resource information at the prompts; no
real IDs, endpoints or credentials are embedded here:

```powershell
Set-Location '<your-isolated-checkout>\labs\lab-13-workiq-files\code'

$secureValue = Read-Host 'Teams transport app client secret VALUE' -AsSecureString
try {
    $env:AzureAd__ClientCredentials__0__ClientSecret =
        [System.Net.NetworkCredential]::new('', $secureValue).Password
} finally { $secureValue.Dispose(); Remove-Variable secureValue }

$secureValue = Read-Host 'WorkIQ blueprint app client secret VALUE (verify its client ID)' -AsSecureString
try {
    $env:WorkIQAgent__Blueprint__ClientCredentials__0__ClientSecret =
        [System.Net.NetworkCredential]::new('', $secureValue).Password
} finally { $secureValue.Dispose(); Remove-Variable secureValue }

$env:NaturalLanguage__Enabled = 'true'
$env:NaturalLanguage__DirectMcp__Enabled = 'true'
$env:NaturalLanguage__FullWorkIqGuide = 'true'
$env:NaturalLanguage__Endpoint = Read-Host 'Canonical Azure model endpoint ending /openai/v1'
$env:NaturalLanguage__Deployment = Read-Host 'Exact Azure deployment name'
$secureValue = Read-Host 'Matching Azure model resource API key' -AsSecureString
try {
    $env:NaturalLanguage__ApiKey =
        [System.Net.NetworkCredential]::new('', $secureValue).Password
} finally { $secureValue.Dispose(); Remove-Variable secureValue }
```

Secure prompts avoid echoing secrets or placing literal values in command history. **Process
environment variables are still ordinary plaintext**, accessible to the process and inherited
children; this is not encrypted storage. Never print them, paste secrets into chat, save them in
source/scripts, or share full environment/configuration dumps. Closing this terminal does not
clear a running child's inherited environment.

Use a canonical HTTPS endpoint such as `https://<your-resource>.services.ai.azure.com/openai/v1`
or the supported Azure OpenAI host form ending `/openai/v1`, without credentials, query or
fragment. Endpoint, API key and deployment must belong to the intended resource. The deployment
is its **exact configured deployment name**, not merely a model-family label.

With .NET 10 installed, build Release first if its output is missing or the source changed:
`dotnet build .\WorkIqFiles.csproj -c Release` (initial dependency restore may be required).
`--no-build` then uses that existing Release output; configuration-only changes do not require
a rebuild. For the tested GPT-6 Luna deployment, the working launch is:

```powershell
dotnet run --project .\WorkIqFiles.csproj -c Release --no-build --no-launch-profile --urls http://localhost:3983 --NaturalLanguage:ReasoningEffort=none
```

**Explicit `none` was observed to work for this tested GPT-6 Luna deployment** after omitted
effort and explicit `low` were rejected with function tools. Empty/omitted effort is not the
same as explicit `none`. This is not a universal GPT-6 Luna capability statement or a changed
code default; verify other deployments' contracts. The user-reported successful AU run selected
three native calls (channel `filesFolder`, folder `children`, and `fetch_blob`), used a local
skill reference, and produced a final summary without human fallback. That observation does not
establish every file format, permission or model configuration.

Settings are bound at startup: after changing them, the operator must restart only the intended
lab to use them. This guide does not start/stop a host or modify a tunnel, endpoint or consent.
`--no-launch-profile` neither suppresses inherited environment nor selects Release. With the
standard `WebApplication.CreateBuilder` setup, `DOTNET_ENVIRONMENT` takes precedence over
`ASPNETCORE_ENVIRONMENT`; do not infer Development from the latter alone.

An optional local alternative is an **already provisioned**, approved user-secrets store with
ID `agentic-user-labs-13-workiq-files-isolated`. Default loading is Development-only; selecting
Development does not create a missing store or supply a missing secret. User-secrets are local
development storage, not an encrypted production vault. Provision through approved tooling
without literals in shell history, and keep the nine deliberate AU/provider settings coherent
if that higher-priority store contains old overrides. Never use `dotnet user-secrets list` or
configuration debug dumps in shared diagnostics. For an intentionally provisioned Development
setup, append `--environment Development` to the launch command; do not force Development in
production code or change global machine/user environment just to mask a missing credential.

### Startup and reply troubleshooting

| Symptom | Meaning and next action |
|---|---|
| Expected human-unavailable warning with AU default | `WorkIQ:AllowHumanIdentity=false` is intentional. It is not a bot credential failure; do not enable human sign-in to repair AU transport. |
| `ArgumentNullException: clientSecret` through `BotAuthenticationHandler` during reply | The Teams transport credential is missing from its effective `AzureAd` binding. Check presence of `AzureAd:ClientCredentials:0:ClientSecret` in the launching process/approved store. This stack does not prove a WorkIQ API ran. WorkIQ's separate blueprint credential is also required for its data calls. |
| `AADSTS7000215` / `invalid_client` | Entra rejected the supplied secret for the indicated app. Privately verify the secret Value, matching client ID and validity. The error alone does not distinguish expiry, wrong app or a mistyped value. |
| Native activation diagnostic | Explicitly set all three: `NaturalLanguage:Enabled`, `NaturalLanguage:DirectMcp:Enabled`, `NaturalLanguage:FullWorkIqGuide`. No guarded fallback is enabled for you. |
| Missing `NaturalLanguage:ApiKey` | Supply the separate Azure model resource key, not either Entra app secret. |
| Model SDK `HTTP 0` | No usable HTTP status was captured; cause is unknown. Check the nonsecret endpoint/deployment and network configuration. Do not conclude that the key is invalid from this alone. |
| HTTP 400 `reasoning-tools-incompatible`, provider recommends `none` | Use the verified explicit `--NaturalLanguage:ReasoningEffort=none` for the tested deployment. No automatic retry, model/API fallback or parameter change occurs. HTTP 400 alone does not establish a bad key. |
| Foreign-provider isolation error | Inspect the named nonsecret flag's effective source and deliberately disable that provider here or use its separate lab. Do not remove the isolation guard. |

`AzureModelDiagnostic.ReasoningEvidence` extracts `reportedValues` from the provider's narrowly
recognized **supported/recommended-value clauses**, such as `Supported values are ...` or
`set reasoning_effort to ...`. It is **not the actual transmitted request effort** and does not
prove an exhaustive list of deployment capabilities. Retain safe diagnostic classifications
without publishing raw request bodies, credentials or full configuration.

For a minimal terminal check, print only the environment selectors or a credential-presence
boolean, never the value:

```powershell
$env:DOTNET_ENVIRONMENT
$env:ASPNETCORE_ENVIRONMENT
[bool](-not [string]::IsNullOrWhiteSpace($env:AzureAd__ClientCredentials__0__ClientSecret))
```

That boolean checks this process variable only, not effective JSON/user-secrets binding or
credential validity. A fresh terminal with no process credential and no provisioned store
cannot recover a secret by changing the environment name. Native writes still execute immediately
when activated; permission/ACL, fixed-principal, private-consent and no-replay boundaries above
remain in force.

### Deliberate human opt-in

The old human recipe (`HumanWorkIQ:Enabled=true`, optional `PrivateAnalysis:Enabled=true`,
and a human principal) is no longer sufficient. The new recipe adds **WorkIQ:AllowHumanIdentity=true**.
Use the existing separately configured human OAuth client/callback/approved consent and matched
personal audience; do not copy or print secrets. For personal Direct human operation:

```powershell
dotnet run --project .\WorkIqFiles.csproj -c Release --no-build --no-launch-profile --urls http://localhost:3983 --WorkIQ:AllowHumanIdentity=true --HumanWorkIQ:Enabled=true --NaturalLanguage:DirectMcp:Principal=SignedInHuman --PrivateAnalysis:Enabled=false
```

For channel-to-private human jobs, explicitly enable `WorkIQ:AllowHumanIdentity`,
`HumanWorkIQ:Enabled` and `PrivateAnalysis:Enabled`; the ordinary principal can remain
`AgentUser`. The existing semantic handoff then requires its per-job private approval and
matched human sign-in. An explicit AU request stays AU. Opt-in is capability, not per-job consent;
it does not upgrade stored scopes or replay uncertain work.

With human opt-in off, the existing main Direct model receives explicit human-unavailable guidance.
A request to authenticate as the human must be reported unavailable, not reinterpreted as AU `/me`.
Explicit AU access to a human-owned target remains AU access subject to its own permissions.
There is **no new LLM preflight or keyword parser**: ordinary Direct keeps its catalog/model/tool
call structure. Natural-language caller interpretation remains model behavior, not a deterministic
intent firewall; actual human auth/registration/job execution are capability-gated in code.

## Preserved boundaries

Only WorkIQ MCP accesses Microsoft 365 data in this host. The client does not call Graph directly
or transfer bytes through provider/preauthenticated URLs; WorkIQ itself may internally use Graph.
Entra token acquisition and Teams SDK bot delivery remain necessary and are not data-provider fallbacks.
AU acquisition, AU JWT/context checks, human session isolation, fixed-endpoint/no-redirect transport,
original native argument passthrough, model-selected references, exact continuation handles,
chat/continuation traces, private error receipts and progress/private delivery remain.

The active skill, manifest, 13 references and original 19-file archive are unchanged:
`D6E0CD296E241445BE0C041502F21FAB11434EB0E1A1546C22A075A39338E960`.
Prompt chars/UTF8 bytes: Direct 9771, PrivateNative 9831, PrivateReadOnly 9895,
Guarded compatibility rendering 8963, RoutingOnly 9999. A retained guide mode is not a retained
guarded execution engine. Initial channel-message no-top guidance is not a proven fix for resource400;
server continuation bytes are still preserved exactly.

This publication contains WorkIQ only. Separate Teams MCP and Graph SDK data hosts are not included.
Provider-isolation errors can refer to their separate lab names, but those are not dependencies or
available backends in this tree. Required bot delivery/models/auth helpers are in
[shared infrastructure](../shared/README.md); no Graph data renderer or native Teams MCP adapter is included.

See [current validation and user steps](TESTING.md). This selected publication excludes mixed-provider
development notes, machine-specific evidence and aggregate fixtures that require unpublished sibling
labs. Their original files remain in the separate development checkout; the original 19-file skill
archive is retained here as historical reference only, never active guidance.
