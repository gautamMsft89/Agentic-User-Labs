# WorkIQ-only publication validation

This runner references only `code\WorkIqFiles.csproj` and its required shared library.
It uses installed SDKs with scripted model/MCP/Teams transports, synthetic identities and
in-process OAuth fixtures. It does not require tenant credentials, a running bot or live services.

From this lab directory:

```powershell
dotnet build .\validation\WorkIqFiles.Validation.csproj -c Release -warnaserror
dotnet run --project .\validation\WorkIqFiles.Validation.csproj -c Release --no-build --no-launch-profile
dotnet run --project .\validation\WorkIqFiles.Validation.csproj -c Release --no-build --no-launch-profile -- --workiq-skill
dotnet run --project .\validation\WorkIqFiles.Validation.csproj -c Release --no-build --no-launch-profile -- --workiq-ingress
dotnet run --project .\validation\WorkIqFiles.Validation.csproj -c Release --no-build --no-launch-profile -- --provider-isolation
```

## Scope and accounting

The selected publication Release build completed with zero warnings/errors. Measured synthetic
results for this tree:

| Suite | Passed | Failed |
|---|---:|---:|
| Default all-current | 1085 | 0 |
| `--workiq-skill` | 534 | 0 |
| `--workiq-ingress` | 163 | 0 |
| `--provider-isolation` | 11 | 0 |

The full default runs all included WorkIQ/current shared fixtures. Selectors overlap and must not
be added as a unique total. `--full-workiq-guide` is an alias for `--workiq-skill`, not activation of
the archived full guide.

This publication deliberately excludes the mixed-development aggregate's Teams/Graph provider
fixtures, Graph RSC comparison commands and tests requiring unpublished sibling projects.
Two former focused WorkIQ-guide cases tested guide exclusion in those sibling hosts; those cases
remain outside this publication. Here, positive WorkIQ resources/defaults and negative foreign
provider activation/assembly-reference checks run without sibling projects.

The original mixed-development counts (3200 current aggregate, 536 focused WorkIQ) are not
publication counts. Before that, a historical aggregate measured 3657 passes / 246 failures;
236 was a later projected residual, not a measured full total. Although 187 refusal cases matched
pre-split behavior, 49 historical cases were individually baseline-unverified. Publication isolation
does not retroactively resolve or reclassify those historical failures.

The deleted scripted WorkIQ recipes and guarded execution engine are not supported contracts.
The runner announces their retirement. Their exclusive fixtures are not silently represented as
passing. Still-active Direct, private human/native, auth/OIDC, schema, transport, cancellation,
mutation no-replay, input/content fidelity, progress and private-error checks remain.

## Important retained assertions

Ordinary prose keeps its model-selected native flow; slash data commands (including HTML/entity
wrappers) cannot dispatch model/catalog/data calls. Operational controls remain. AU defaults and
explicit human opt-in preserve fixed caller selection, OAuth/callback/private-worker exclusion
when disabled, private per-job consent and stored-job isolation. Retired flags become inactive
only in memory; warnings disclose bounded allowlisted names, not configuration values.

The narrow inactive/rootless group compatibility still denies AU/human data acquisition,
personal-context reuse and private handoff. Tenant, requester and conversation checks remain.
Model-context fixtures check human-unavailable guidance without an additional intent preflight;
scripted replies do not prove arbitrary real-model intent interpretation is deterministic.

Native sessions retain queue serialization, per-request bearer acquisition, bounded read reconnect,
fixed endpoint/no redirects and no replay of uncertain writes. Exact continuation arguments/handles,
model-visible original results, resource-error attribution and private-only diagnostic receipts remain.
Shared/group/unknown contexts must not receive the detailed private error display.

All fifteen embedded resources, thirteen active references and compact limits are checked against
skill hash `D6E0CD296E241445BE0C041502F21FAB11434EB0E1A1546C22A075A39338E960`.
The original 19-file reference archive is not embedded or active.

## User-owned live steps

1. Follow the current README's fresh-terminal credential/model setup and select only your intended
   host. Build Release when source changes; configure and restart it yourself. Never paste credentials.
2. Use `/help` and `/status`; confirm fixed AU, inactive human capability by default and retired recipes.
   `/signin` and `/private-jobs` must not start human auth/jobs unless explicitly enabled.
3. Send `/file read drive file`, `/folder create legacy-test`, `/confirm old-id` and `/team-graph channels`.
   Expect retired guidance with no native/model dispatch, not a write or confirmation token.
4. Request a plain-language read on an approved synthetic target. Inspect the chosen native tools,
   original arguments, fixed principal and continuation/diagnostic receipts.
5. Request a change only if you intend real effects. Native writes can execute immediately; do not
   replay an uncertain action merely because a response failed.
6. For deliberately enabled human work, retain matched personal sign-in and per-job private consent.
   Cancel via actual job controls; `/cancel old-id` is not private-job cancellation.
7. Future provider errors may include bounded private receipts in verified personal delivery only.
   Historical missing-receipt investigation remains separate; no past raw error is reconstructed.
