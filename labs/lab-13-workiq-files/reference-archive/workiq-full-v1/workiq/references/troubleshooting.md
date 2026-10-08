# Troubleshooting WorkIQ

**Lab13 adaptation:** current host mode and native evidence govern this
reference. Generic error codes do not identify a permission, URL or schema
cause. `ask`, arbitrary downloads, agent/header overrides and alternate
identity/provider fallbacks remain unavailable. Never replay uncertain writes
after sign-in, consent, schema or endpoint changes.

Use this reference when a WorkIQ tool call fails or behaves unexpectedly.

## Tool name not found

**Symptom:** A call to `ask`, `fetch`, etc. fails with "tool does not exist" or similar.

**Possible cause:** another host may use prefixed names; the error alone does not establish that explanation.

**Fix:** use only an exact offered eligible Lab13 descriptor. `ask` and prefixed delegates remain excluded. A missing tool does not authorize reconnecting, switching hosts or retrying an operation.

## Entity tool returns a 400 / "bad request" on a Graph URL

**Symptom:** `fetch` or another entity tool returns HTTP 400 with a parser or validation error.

**Possible causes:** request shape, unsupported parameters/path, schema or server validation. HTTP 400 alone does not prove malformed encoding.

**Fix:** Verify the URL:

1. Matches the actual supported descriptor/path and mode. `/me` and `/users` are examples, not an exhaustive root allowlist; do not infer version support from this error.
2. All query parameter values are URL-encoded (spaces → `%20`, quotes → `%27`, etc.).

See the **URL Format Rules** section of `SKILL.md` for full examples.

## Tool call fails with a `null` / empty response and no error details

**Symptom:** A WorkIQ tool call fails but the response is literally `null` — no status code, no error body, no diagnostic of any kind.

**Cause:** Some backend failures (permission denials, unsupported paths, policy blocks, timeouts) are currently surfaced as a bare `null` response instead of an error message.

**Fix / how to proceed:**

1. For an idempotent read, check the request first — URL format rules,
   URL-encoded query values, and that the path/ID is real (no `{id}` literals
   or guessed IDs). Fix and retry **once**.
2. If a multi-URL `fetch` failed, retry the URLs individually — one bad URL can fail the batch.
3. For `create_entity`, `update_entity`, `delete_entity`, or `do_action`, a
   `null`, timeout, or other ambiguous response does **not** prove that the
   mutation failed. **Do not replay it.** Use a safe read to reconcile the
   affected resource or state when possible.
4. If reconciliation cannot determine whether the mutation happened, stop and
   report the outcome as **indeterminate**. Ask the user how to proceed rather
   than risking a duplicate or repeated side effect.
5. Do not probe many path variants, other backends, or alternative APIs hunting
   for a way around the failure.
6. **Report it honestly:** tell the user which call failed and that the server returned no diagnostic detail. You may suggest possible causes (missing Graph scopes, unsupported path) only as explicitly unconfirmed hypotheses. **Never state a specific status code or error ("403", "AccessDenied", "Insufficient privileges") that you did not actually observe in a tool response.**

## `search_paths` rejects a `backend` / `source` / `provider` argument

**Symptom:** `search_paths` returns a tool input validation error, or silently ignores extra arguments like `backend: "sharepoint-rest"` / `provider: "dataverse"`.

**Cause:** use the actual descriptor's query/filter contract; backend/source/
provider arguments must not be invented. Agent overrides are blocked here.
No Graph-only catalog claim follows from an argument error.

**Fix:** For a definitive pre-dispatch argument rejection, correct to the
advertised safe discovery contract only. Report confirmed surfaces and
unconfirmed limitations; never invent a backend or bypass a policy denial.

## `fetch_blob` returns "tool does not exist"

**Symptom:** A call to `fetch_blob` returns "tool does not exist", or the tool is missing from the available-tools list.

**Cause:** the required descriptor is absent; neither global release status nor a stale catalog is established by that fact.

**Fix:** report that the required native download capability is unavailable in the current mode/catalog. Do not reconnect, invent variants such as `download_file`/`get_blob`, or download provider URLs externally.

## `upload_blob` returns "tool does not exist"

**Symptom:** A call to `upload_blob` or a variant such as `put_file` returns "tool does not exist".

**Cause:** this runtime does not expose `upload_blob`; that does not prove a global release restriction or absence of every upload-related native action.

**Fix:** report the missing source/transfer contract precisely. The limited guarded slash-command synthetic upload does not enable arbitrary model attachment upload. Do not retry guessed tools or perform an external PUT; a permitted destination `webUrl` may support a manual user action but is not upload success.

## `ask` is slow or appears to hang

**Symptom:** A single call to `ask` takes 10–30 seconds.

**Reference context:** an agentic delegate may perform multiple backend searches. These historical timings do not measure this runtime; `ask` is excluded.

**Fix:** `ask` is unavailable here. Eligible structured reads may avoid excess
orchestration, but entity calls can take many seconds. No latency is guaranteed.

## `ask` times out around 300 seconds

**Symptom:** `ask` fails with a timeout after ~300 seconds, or repeatedly hits the request time limit on complex questions.

**Reference context:** broad questions can require substantial orchestration, but a timeout alone does not establish its cause. `ask` is not offered in Lab13.

**Fix:** do not call or split unavailable delegation. Report the limitation; eligible bounded structured reads may support the original request without broadening its scope. No 300-second runtime budget or latency promise follows from this historical example.

## Authentication or consent errors

**Symptom:** Tool calls fail with auth, consent, or permission errors.

**Cause:** The WorkIQ MCP server requires tenant admin consent on first use, and the current user must be signed in.

**Fix:** Report the existing host's sign-in boundary. The original package
referenced a Tenant Administrator Enablement Guide outside this bundle; no
external document is automatically loaded. Do not initiate consent, broaden
grants or retry an uncertain operation. A new request uses existing host
authorization and the fixed matched identity.

## HTTP 403 Forbidden on an entity tool call

**Symptom:** `fetch`, `do_action`, `update_entity`, or another entity tool returns `HTTP 403` for a Graph path. Two common flavors:

1. **Missing delegated scope** — error body contains `"Missing scope permissions on the request. API requires one of '<Scope.Name>, ...'"`. Typical examples: editing a channel message requires `ChannelMessage.ReadWrite`; reading another user's calendar requires `Calendars.Read.Shared`.
2. **Insufficient directory privileges** — error body contains `"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."`. Typical examples: `PATCH /me` to change `jobTitle`, `department`, `officeLocation`, `manager`, or any other directory-managed property -- these are read-only via delegated `/me` scopes and only an admin can write them through the directory.

**Interpretation:** a 403 can reflect permissions, resource access, policy or another service restriction. Do not infer a particular missing grant, directory role or remediation from the status or `Authorization_RequestDenied` alone. The example bodies above are not authoritative permission mappings for the current endpoint.

**Do not work around or automatically retry the denial.** Surface observed evidence, not a claim that every 403 is permanent or fixed by consent:

- If the actual body explicitly names a missing scope, report that diagnostic without requesting a broader grant.
- Otherwise report the denial and uncertainty; do not invent a directory/admin requirement.

**Next step:** any permission or administrator action is separate user-owned work. This guide changes no grants, identities or sign-in flow, and does not authorize an alternate endpoint, principal or provider.

Even after a separately authorized configuration change, never automatically replay a write whose outcome was uncertain.
