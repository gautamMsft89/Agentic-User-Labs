# Lab 13 — Ask Otto in Plain Language (WorkIQ MCP)

In this lab you talk to your agent user (we call it **Otto**) in Microsoft Teams in normal sentences:

> "What did we finally decide about the Aurora incident, and who is the SME?"

Otto answers by reading Microsoft 365 work data through **WorkIQ**, signed in as **itself** (its own agent-user identity).
No slash commands, no keyword rules: a language model reads your message, picks WorkIQ tools from WorkIQ's own catalog,
and writes the answer back into the same chat or channel thread.

```text
Teams message ──► this app ──► language model ──► WorkIQ MCP tools (as Otto) ──► answer in the same Teams conversation
```

Time: about 60 minutes the first time (most of it is preparing demo messages in Teams).

---

## Step 1 — Fill in `lab13.config.json`

All agent-user, WorkIQ MCP and model settings live in **one file**. From the repo root:

```powershell
cd labs\lab-13-workiq-natural-language\code
Copy-Item lab13.config.example.json lab13.config.json
notepad lab13.config.json
```

Replace every `<...>` value. `lab13.config.json` is git-ignored; never commit it. The app loads it automatically at
startup (from the folder you run `dotnet run` in, i.e. `code`).

**Teams / agent user** (Entra admin center, <https://entra.microsoft.com>)

| Field | Where to find it | Example format |
|---|---|---|
| `AzureAd:TenantId` | Overview → **Tenant ID** | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` (GUID) |
| `AzureAd:ClientId` | App registrations → your blueprint → **Application (client) ID** | GUID |
| `AzureAd:ClientCredentials[0]:ClientSecret` | Blueprint → Certificates & secrets → the **Value** column (not *Secret ID*; shown only once) | `Abc8Q~...` (~40 chars) |
| `WorkIQAgent:AgentIdentityClientId` | Enterprise applications → your agent identity → **Application ID** | GUID |
| `WorkIQAgent:AgentUserObjectId` | Users → Otto → **Object ID** (not a client ID, not the email) | GUID |
| `Host:TunnelUrl` | `devtunnel show <tunnel-name>` → the URL for port 3986 | `https://abc123-3986.usw2.devtunnels.ms` |
| `Urls` | Local port the tunnel forwards to; keep unless the port is taken | `http://localhost:3986` |

**WorkIQ MCP** (prefilled — change only if told to)

| Field | Value |
|---|---|
| `WorkIQ:Endpoint` | `https://workiq.svc.cloud.microsoft/mcp` |
| `WorkIQ:Resource` | `api://workiq.svc.cloud.microsoft` |
| `WorkIQ:Scope` | `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask` (if empty, built from `Resource`) |

**Model** (Microsoft Foundry / Azure OpenAI portal; any chat model with tool calling — see section 6)

| Field | Where to find it | Example format |
|---|---|---|
| `Model:Endpoint` | Resource → **Endpoint**, plus `/openai/v1` | `https://my-resource.openai.azure.com/openai/v1` |
| `Model:Deployment` | **Deployments** → name | `gpt-4.1` |
| `Model:ApiKey` | **Keys and Endpoint** → Key 1 | 32+ character key |
| `Model:ReasoningEffort` | Optional; `none` for reasoning models that reject tools | empty or `none` |

**Client ID vs. object ID:** client IDs identify apps; the object ID identifies Otto the user. Mixing them up is the
most common mistake. If the file is missing, startup prints the `Copy-Item` command. If values are still `<...>` or empty,
it lists them by section and exits.

---
## 1. Words used in this lab

| Word | Meaning |
|---|---|
| **Agent user (AU)** | A real Microsoft 365 user account that belongs to an agent. Here it is Otto. It has its own mailbox, chats and Teams membership. |
| **Blueprint** | The Entra app registration that owns your agent. Your code signs in with its client ID and secret. |
| **Agent identity** | A second Entra identity created from the blueprint. Its **client ID** is needed to get Otto's token. |
| **WorkIQ** | A Microsoft service that lets an agent read and act on Microsoft 365 data (chats, channels, mail, files, people). |
| **MCP** | Model Context Protocol. WorkIQ publishes a list of **tools** (for example `fetch`, `retrieve`, `create_entity`); a model can call them. |
| **Skill** | The short guide in [`code/workiq/SKILL.md`](./code/workiq/SKILL.md) that teaches the model how to use WorkIQ well. |
| **Dev tunnel** | A public HTTPS address that forwards Teams messages to your laptop. |

---

## 2. Before you start

Who does what:

| Task | Who | Where it is explained |
|---|---|---|
| Install .NET 10 SDK, dev tunnel CLI | You | [Lab 0](../lab-00-environment-setup/Readme.md), [dev tunnel](../../dependencies/dev-tunnel/Readme.md) |
| Create blueprint, agent identity and agent user (Otto) with a Microsoft 365 license | Tenant admin (or you, if you have the rights) | [Lab 1](../lab-01-create-digital-worker/Readme.md) |
| Messaging endpoint for the blueprint points to your tunnel | You | [Lab 2](../lab-02-make-agent-respond/Readme.md) |
| WorkIQ permission `WorkIQAgent.Ask` consented for Otto | Tenant admin | [Lab 6.1](../lab-06.1-workiq/Readme.md) explains WorkIQ consent |
| A chat model deployment that supports **tool calling** (Azure OpenAI / Microsoft Foundry or any OpenAI-compatible endpoint) | You or your team | Section 6 below |

You do **not** need to run Labs 13, 14 or 15. This lab is fully standalone.

---

## 3. Prepare demo data

Follow [scenarios.md](./scenarios.md) part A. It gives paste-ready messages for a small "Aurora" incident across a
channel thread and a group chat. Add Otto to that team/channel and to the group chat. Wait 15–30 minutes after posting so
WorkIQ can index the messages (search can lag behind; direct reads by link usually work immediately).

---

## 4. Run the lab

1. Start your dev tunnel to port **3986** (keep it running):

   ```powershell
   devtunnel host <your-tunnel-name> --port-numbers 3986 --allow-anonymous
   ```

   Your blueprint's messaging endpoint must be `https://<tunnel-host>/api/messages`.
2. Start the app:

   ```powershell
   cd labs\lab-13-workiq-natural-language\code
   dotnet run
   ```

3. Read the startup output. You should see `Settings loaded from ...`, the messaging endpoint, then
   `WorkIQ connected as the agent user. Tools (N): ask, retrieve, fetch, ...` (the names come live from WorkIQ). If the
   catalog fails, the app prints the error and keeps running; the next Teams message tries again.
4. In Teams, open a 1:1 chat with Otto and send: **"What can you do with WorkIQ?"**

Where Otto listens:

| Place | Otto replies when |
|---|---|
| 1:1 (personal) chat | every message |
| Group chat | the message **@mentions Otto**; with ambient listening on, also when an overheard message is aimed at Otto or implies something Otto can do; actions are proposed and confirmed in the chat first (otherwise it stays silent) |
| Channel post or thread reply | same as group chat; the reply goes to the same thread |

Otto handles each message as it arrives. A request can take up to a few minutes when many tools are called.

### When the tunnel address changes

1. Update the blueprint messaging endpoint to the new `https://<tunnel-host>/api/messages`. If you use ambient
   listening, re-run the `PUT` below with the new `callbackUri`.
2. Update `Host:TunnelUrl` in `lab13.config.json` and restart `dotnet run` (the startup output shows the endpoint to use).
3. Send a 1:1 message to Otto to confirm.

### Let Otto listen without @mention (ambient)

By default Teams sends Otto only 1:1 messages and @mentions. To let Otto hear **every** message in group chats and
channels it belongs to, set the blueprint's communication configuration with the **beta** Microsoft Graph
[agentCommunicationConfiguration](https://learn.microsoft.com/en-us/graph/api/resources/agentcommunicationconfiguration?view=graph-rest-beta)
API. This is a one-time admin step outside the app. The lab code never calls Microsoft Graph.

| Item | Value |
|---|---|
| Permission | Delegated `AgentCommunicationConfiguration.ReadWrite` or application `AgentCommunicationConfiguration.ReadWrite.All`, usually granted by an admin |
| Tool | [Graph Explorer](https://developer.microsoft.com/graph/graph-explorer) or `Invoke-MgGraphRequest` |
| `{agentIdentityBlueprintId}` | The blueprint application id as used in the docs ([Update](https://learn.microsoft.com/en-us/graph/api/agentidentityblueprint-update-communicationconfiguration?view=graph-rest-beta)) |

Set it. The `PUT` is a **full replace**, so any property you leave out is cleared. Always include the endpoint.

```http
PUT https://graph.microsoft.com/beta/applications/{agentIdentityBlueprintId}/microsoft.graph.agentIdentityBlueprint/communicationConfiguration
Content-Type: application/json

{
  "isOverridableAtAgentIdLevel": false,
  "endpointConfiguration": {
    "configurationType": "apiBased",
    "apiBased": { "callbackUri": "https://<tunnel-host>/api/messages" }
  },
  "teamworkConfiguration": {
    "groupChatConfiguration": { "messageNotificationMode": "allMessages" },
    "channelConfiguration": { "messageNotificationMode": "allMessages" },
    "oneOnOneChatConfiguration": { "messageNotificationMode": "allMessages" },
    "meetingChatConfiguration": { "messageNotificationMode": "allMessages" }
  }
}
```

Verify it with [Get](https://learn.microsoft.com/en-us/graph/api/agentidentityblueprint-get-communicationconfiguration?view=graph-rest-beta):

```http
GET https://graph.microsoft.com/beta/applications/{agentIdentityBlueprintId}/microsoft.graph.agentIdentityBlueprint/communicationConfiguration
```

| Notes | |
|---|---|
| Per agent identity | An agent identity can have its own override ([Get](https://learn.microsoft.com/en-us/graph/api/agentidentity-get-communicationconfiguration?view=graph-rest-beta) / [Update](https://learn.microsoft.com/en-us/graph/api/agentidentity-update-communicationconfiguration?view=graph-rest-beta)) when `isOverridableAtAgentIdLevel` allows it. [reset](https://learn.microsoft.com/en-us/graph/api/agentcommunicationconfiguration-reset?view=graph-rest-beta) removes the override and restores the blueprint setting. |
| New tunnel | Re-run the `PUT` with the new `callbackUri`. |
| What Otto does | Overheard messages reach the model marked `[overheard]`. The model decides by intent: it replies when the message is aimed at Otto or implies something Otto can do, and otherwise returns `NO_REPLY`, so nothing is posted. Before any action, Otto posts a proposal in the chat and waits for a participant to reply "yes". Try it with [scenarios.md](./scenarios.md) exercise 8. |

#### Choose which un-mentioned messages Otto answers

| Step | What to do |
|---|---|
| What Otto answers today | The [`SKILL.md`](./code/workiq/SKILL.md) section "Overheard messages" decides by intent. **Reply:** aimed at Otto (by name or context), implies something Otto can do, or answers his earlier proposal. **Silent:** people talking to each other, small talk, opinions, status updates, nothing to add, or unsure. |
| Actions | Otto never acts straight from an overheard message. He proposes in the chat (what, where, 1–3 line draft, "Reply yes to go ahead") and acts only after a participant confirms. That reply needs no @mention. Read-only answers come directly. |
| Change it | Edit that section in plain words. |
| Apply the change | `SKILL.md` is built into the app, so restart it: Ctrl+C, then `dotnet run`. |
| Test | In the group chat, post `Otto, what WorkIQ tools do you have?` **without** @mentioning Otto. |

#### Otto didn't reply? Read the console

| Console shows | Meaning | Fix |
|---|---|---|
| No `[in] message …` line | Teams never delivered the message | Run the `GET` above: `allMessages` and `callbackUri` = current tunnel + `/api/messages`. Check the tunnel is running. |
| `[admit] dropped: <reason>` | Rejected by that check (tenant, recipient is not Otto, sender, empty text) | Fix what the reason names, for example the tenant or Otto's object ID in `lab13.config.json`. |
| `[agent] Overheard → NO_REPLY` | The model chose to stay silent | Reword the message, or adjust "Overheard messages" in `SKILL.md` and restart. |
| `[in] <other type> name=…` | A non-message activity arrived (for example a conversation update) | Usually nothing to do. Otto answers only `message` activities. |
| `[card] to=… status=err …` | WorkIQ rejected the card post | Read the error; Otto retries once after `get_schema`. |
| `[stream] failed: …` | 1:1 streaming isn't supported here | Nothing to do; Otto falls back to one normal reply. See "Streaming (1:1)". |

### Adaptive Cards (via WorkIQ)

Ask Otto for an answer "as an Adaptive Card". The model builds an Adaptive Card v1.5 and posts it itself with WorkIQ
`create_entity`, as Otto, to the current chat, channel or thread (`SKILL.md` "Adaptive Cards"). The Teams SDK does not
send it. The message body is a short HTML summary plus `<attachment id="c1">`, and that summary is the notification preview.
Otto then replies `NO_REPLY`, so you see only the card.

| Item | Detail |
|---|---|
| Purpose | Tests how Graph-posted cards appear in notifications: desktop toast, Activity feed, chat-list preview and mobile push |
| Card type | Display-only: text, facts and `Action.OpenUrl`. No `Action.Submit`/`Action.Execute`, because Otto gets no callbacks from Graph-posted cards |
| 1:1 chat | The bot conversation id is not a Graph chat id; Otto looks the chat up in `/me/chats` |
| Console | `[card] to=<path> status=ok`, then `[agent] NO_REPLY (already posted via WorkIQ)`; on failure `status=err <first 200 chars>` |

### Streaming (1:1)

In a 1:1 chat Otto shows progress while WorkIQ works: "Working on it…" right away, then a short status for each WorkIQ
tool call ("Searching mail…", "Reading Teams messages…", "Looking at files…", "Posting…"). Then the answer streams in and
ends as **one** final message. `Program.cs` class `Progress`.

| Item | Detail |
|---|---|
| Where | 1:1 only. In group chats and channels, and for overheard messages, Otto sends one normal reply without progress. |
| Card posted (`NO_REPLY`) | The stream ends with "Done - see above." |
| Limits | Teams web and desktop; iOS/mobile streaming is still rolling out. Teams ends a stream after 2 minutes, so if WorkIQ takes longer the answer comes as one normal message. If anything fails, Otto sends one normal reply. |
| Console | Every step shows elapsed time and the Teams activity id: `[stream] start`, `[stream] status "<text>" #<seq> t=<s> id=<id>`, `[stream] chunk #<seq> chars=<n>`, `[stream] final chars=<n> id=<id>`. Fallback: `[stream] failed: http <status> <error> → normal reply`, `[stream] not confirmed …` or `[stream] past the 2-minute stream limit …`. |

### Create Otto from the app package (manifest) and add it to Teams

Optional. Instead of creating Otto by hand (Lab 1), publish the blueprint as an Agent 365 app and **hire** Otto from
it. Each hire creates a **new** agent identity and agent user, so you then point this lab at the new ids.

**1. Package.** `manifest/` contains:

| File | Purpose |
|---|---|
| `manifest.json` | Teams devPreview app manifest; `agenticUserTemplates` points to the template below |
| `agenticUserTemplateManifest.json` | `agentIdentityBlueprintId` = `<BLUEPRINT_APP_ID>` placeholder; `communicationProtocol` = `activityProtocol` |
| `color.png`, `outline.png` | Icons, 192×192 and 32×32 |
| `build-package.ps1` | Validates the JSON and icons, writes the zip |

```powershell
.\manifest\build-package.ps1                          # blueprint = AzureAd:ClientId from code\lab13.config.json
.\manifest\build-package.ps1 -BlueprintAppId <guid>   # or pass it
```

Output: `manifest\out\otto-lab13.zip` (4 files at the zip root). The real blueprint id goes into the zip only;
`manifest/out/` is git-ignored.

**2. Upload, hire, wire up.**

| # | Step | Where / how |
|---|---|---|
| 1 | Upload the zip (Global Admin) | [admin.microsoft.com](https://admin.microsoft.com) → **Agents** → **All agents** → **Upload custom agent**. Needs a Frontier / Agent 365 preview tenant; the upload fails in other tenants. Otto appears after 5–10 min. |
| 2 | Create an instance (hire) | **Agents** → **All agents** (Agent Registry) → **Otto** → **Create instance**: manager, license, customize, review. Creating the agent user can take several minutes. |
| 3 | Find the new agent identity | `GET https://graph.microsoft.com/beta/servicePrincipals/microsoft.graph.agentIdentity?$filter=agentIdentityBlueprintId eq '<BLUEPRINT_APP_ID>'` → `id` (for agent identities `appId` = `id`) |
| 4 | Find the new agent user | `GET https://graph.microsoft.com/beta/users/microsoft.graph.agentUser?$filter=identityParentId eq '<AGENT_IDENTITY_ID>'` → `id` |
| 5 | Grant consent for the new identity | Two `POST /v1.0/oauth2PermissionGrants` (body below): Teams messaging `AgentData.ReadWrite` and WorkIQ `WorkIQAgent.Ask` |
| 6 | Update `code\lab13.config.json` | `WorkIQAgent:AgentIdentityClientId` = step 3 id, `WorkIQAgent:AgentUserObjectId` = step 4 id |
| 7 | Check the endpoint | Blueprint messaging endpoint = `<tunnel>/api/messages` ([Lab 2](../lab-02-make-agent-respond/Readme.md)) |
| 8 | Run | `devtunnel host <tunnel-id>`, then `dotnet run` |
| 9 | Add Otto to Teams | Search Otto's UPN in Teams, start a 1:1 chat, then add him to group chats or teams |

Consent body for step 5 (once per resource):

```json
POST https://graph.microsoft.com/v1.0/oauth2PermissionGrants
{
  "clientId": "<AGENT_IDENTITY_ID>",
  "consentType": "AllPrincipals",
  "resourceId": "<RESOURCE_SP_OBJECT_ID>",
  "scope": "AgentData.ReadWrite"
}
```

`resourceId` is the resource's service principal **object id**, which differs per tenant. Look it up:

| Scope | Lookup |
|---|---|
| `AgentData.ReadWrite` | `GET /v1.0/servicePrincipals?$filter=appId eq '5a807f24-c9de-44ee-a3a7-329e88a00ffc'&$select=id` |
| `WorkIQAgent.Ask` | `GET /v1.0/servicePrincipals?$filter=servicePrincipalNames/any(n:n eq 'api://workiq.svc.cloud.microsoft')&$select=id` |

| Problem | Check |
|---|---|
| Upload fails | Tenant isn't Frontier / Agent 365 preview; zip files not at the root; not Global Admin |
| `[admit] dropped: recipient is not Otto` | Config still has the old identity / agent user ids; repeat steps 3–6 and restart |
| No `[in]` line at all | Tunnel not hosted or endpoint wrong (steps 7–8), or the agent user was made by hand instead of hired from this package's blueprint |
| `AADSTS65001` / WorkIQ 401/403 | Consent (step 5) or license missing for the new identity / agent user |

---

## 5. Exercises

The exact prompts and expected facts are in [scenarios.md](./scenarios.md) part B. In order:

1. **Capabilities** — ask Otto what it can do; it describes the WorkIQ tools it actually has.
2. **General question** — "What was decided about Aurora?" Otto searches what *it* can see.
3. **Scoped to a chat** — paste a group-chat link; Otto reads only that chat.
4. **Scoped to a channel thread** — @mention Otto inside the incident thread; it reads that thread.
5. **Private confirmation** — ask in the channel to create an incident group; Otto proposes the plan, and asks you to confirm in your 1:1 chat.
6. **Approve / decline / revise** — answer Otto in the 1:1 chat.
7. **Create the incident group and post a summary** — after you approve.
8. **Ambient incident post** — two people discuss an ICM without @mentioning Otto; when one says "We need to create a
   post about this ICM in Aurora Incidents", Otto proposes a draft in the chat, you reply "yes", and Otto posts it (needs the ambient `PUT`).
9. **Adaptive Card notifications** — "@Otto send me the Aurora status as an Adaptive Card" in 1:1, a group chat and a
   channel; check the toast, Activity feed and mobile push.
10. **Streaming (1:1)** — "summarize everything about the Aurora incident across mail, chats and files" in 1:1; watch
   status updates and a single final answer.

---

## 6. Use your own model

Any chat-completions model that supports **function (tool) calling** works. Set `Model:Endpoint`, `Model:Deployment`,
`Model:ApiKey`. If your model rejects tools with reasoning turned on (some reasoning models do), set
`Model:ReasoningEffort` to `none`. Models without tool calling cannot do this lab — Otto would only chat, never read data.

---

## 7. How it works (for the curious)

| File | Job |
|---|---|
| [`code/Program.cs`](./code/Program.cs) | Starts the Teams bot host, prints the WorkIQ catalog, sends each admitted message to the agent and posts the answer back (nothing for a silent overheard message). |
| [`code/lab13.config.example.json`](./code/lab13.config.example.json) | Template for all settings (copy to `lab13.config.json`). |
| [`code/LabSetup.cs`](./code/LabSetup.cs) | Loads `lab13.config.json` and lists any missing values by section. |
| [`code/Agent.cs`](./code/Agent.cs) | Admits messages (tenant, Otto as recipient, not from Otto; marks un-mentioned ones as overheard). Runs the model ↔ WorkIQ tool loop. Keeps short per-conversation memory and the group → 1:1 handoff. |
| [`code/WorkIqConnection.cs`](./code/WorkIqConnection.cs) | Gets Otto's own WorkIQ token and talks MCP to WorkIQ. |
| [`code/workiq/SKILL.md`](./code/workiq/SKILL.md) | The skill text the model reads. Edit it to change Otto's behaviour. |

Design rules this lab follows:

- **Otto's own identity only.** The WorkIQ token is requested for the agent user (`WithAgentUserIdentity`). No human
  sign-in and no app-only token, so Otto sees only what Otto has access to.
- **WorkIQ only.** The app never calls `graph.microsoft.com` itself. WorkIQ uses Microsoft Graph internally.
- **Native tools, no retries.** The model gets WorkIQ's full tool list, its arguments are forwarded unchanged, and a
  failed call is reported, never repeated automatically.
---

## 8. Troubleshooting

Errors in Teams look like `Sorry, something went wrong: <message>`. Errors for overheard messages appear only in the
app console.

| Symptom | Usual cause | Fix |
|---|---|---|
| App exits and lists values by section | Values in `lab13.config.json` still empty or `<...>` | Step 1 |
| Message mentions MSAL / `AADSTS` | Wrong agent identity client ID, Otto object ID or secret value, or WorkIQ consent missing | Re-check Step 1; ask the admin about `WorkIQAgent.Ask` consent |
| Message mentions 401/403 from WorkIQ | Otto lacks a license or WorkIQ access | Admin check |
| Message mentions 401/404 from the model, or tools/reasoning | Wrong key, endpoint (missing `/openai/v1`) or deployment; model rejects tools with reasoning | Section 6; try `Model:ReasoningEffort` = `none` |
| Error after Otto started a create/post/update | A write may already have happened | Check in Teams before asking again |
| Answer says "I can't see that chat" | Otto is not a member, or the data isn't indexed yet | Add Otto; wait; paste the exact link |
| No reply in a group/channel | You didn't @mention Otto (and ambient listening is off) | @mention Otto, or set up ambient listening |
| Ambient messages never reach the app | Ambient config missing, or `callbackUri` points to an old tunnel | Run the `GET` (section 4) and check `allMessages` and `callbackUri`; re-run the `PUT` |
| WorkIQ fails until restart | The MCP session expired | Restart `dotnet run` |
| No reply anywhere | Tunnel down or messaging endpoint wrong | Section 4 |
---

## 9. Limitations

- Memory (recent turns and pending handoffs) is in-process and lost when the app restarts.
- The confirm-before-acting behaviour comes from the skill and the model; it is demonstrated behaviour, not a hard lock
  in code. Use test data.
- Without the ambient `PUT` (section 4), Otto only hears 1:1 messages and @mentions. With it, the model decides by intent
  when to answer overheard messages and asks for confirmation in the chat before acting. Use test data.
- There is no REST or agent-to-agent endpoint.
- Otto's replies are sent as Markdown. Posts he creates through WorkIQ (channel or chat messages) are formatted only as HTML, so `SKILL.md` tells him to send `contentType: html`. If a post shows as one paragraph, the model skipped that.
- WorkIQ search can lag behind new messages; tenant policy may block creating groups or posting.
- Which tools exist is decided by WorkIQ at runtime; your catalog may differ from the examples here.
