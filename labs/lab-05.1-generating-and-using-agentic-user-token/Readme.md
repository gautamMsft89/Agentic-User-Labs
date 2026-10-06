# Lab 5.1 - Generating and Using Agentic User Token

⏰ Estimated time: 50 min

## Overview

This lab complements [Lab 05 - Call Microsoft Graph](../lab-05-Graph-Api/Readme.md). Lab 05 uses a separate application's token; Lab 5.1 generates and uses the agent's own user token through the Microsoft Identity Web SDK.

In this standalone lab, you will extend the streaming agent pattern from Lab 03 with a Microsoft Graph call authenticated as the agent's user account. When a user sends a message, the application:

1. Reads the sender's Microsoft Entra object ID and the recipient's agent identity from the incoming activity.
2. Sends an informative update while it calls Microsoft Graph.
3. Acquires an agent-user delegated token and retrieves the sender's display name and email address.
4. Streams the result back to Microsoft Teams word by word.

You do not need to complete the earlier labs first if you already have an agent blueprint, an agent identity with a linked agentic user, the .NET 10 SDK, and Microsoft dev tunnel. Otherwise, follow [Lab 01](../lab-01-create-digital-worker/Readme.md) to create the digital worker first. An administrator must grant the delegated Graph permission described below.

> This sample uses the blueprint-to-agent-to-agent-user token exchange, ending in the `user_fic` grant. The Graph token represents the agent's user account, not the blueprint application or the human sender. No separate Graph app registration or human sign-in is required.

## Step 1: Get the lab code sample

Clone this repository to your local development environment, then open the `labs/lab-05.1-generating-and-using-agentic-user-token/code` folder.

The folder contains a complete .NET application:

- `Program.cs` configures the Teams application and writes the streaming response.
- `IAgentOrchestrator.cs` defines the streaming event and orchestrator contracts.
- `GraphAgentOrchestrator.cs` coordinates the Graph lookup and response updates.
- `GraphService.cs` authenticates with Microsoft Entra ID and calls Microsoft Graph.
- `appsettings.json` contains the agent blueprint and local server settings.

### How the sample works

`Program.cs` creates a streaming writer and routes informative and response events:

```csharp
TeamsStreamingWriter stream = TeamsStreamingWriter.CreateFromContext(context);

await foreach (IAgentEvent update in agent.GetUpdatesAsync(
    context.Activity,
    cancellationToken))
{
    if (update.IsInformative)
    {
        await stream.SendInformativeUpdateAsync(update.Text, cancellationToken);
    }
    else
    {
        await stream.AppendResponseAsync(update.Text, cancellationToken);
    }
}
```

`GraphAgentOrchestrator.cs` obtains the sender ID and the recipient's agent identity from the message activity, then asks the Graph service for the sender's profile:

```csharp
string userId = activity.From?.AadObjectId ?? string.Empty;

UserProfile profile = await _graphService.GetUserProfileAsync(
    userId,
  activity.Recipient?.GetAgenticIdentity(),
    cancellationToken);
```

`AddTeamsBotApplication()` registers Microsoft Identity Web token acquisition, token caching, and agent-identity support using the `AzureAd` blueprint configuration. `Program.cs` registers the Graph service and orchestrator with dependency injection to reuse that token provider.

`GraphService.cs` uses `IAuthorizationHeaderProvider` with `WithAgentUserIdentity(...)`. The `Guid` overload identifies the agent user by object ID; the string overload is for a UPN. The installed `Microsoft.Identity.Web.AgentIdentities` SDK handles these exchanges and caches tokens:

1. Acquires the blueprint exchange token (T1) with the agent identity as `fmi_path`.
2. Uses T1 to acquire the agent identity exchange token (T2).
3. Uses T1 and T2 with `grant_type=user_fic` to acquire the agent-user delegated Graph token.

```csharp
AuthorizationHeaderProviderOptions options = new AuthorizationHeaderProviderOptions
{
  AcquireTokenOptions = new AcquireTokenOptions
  {
    AuthenticationOptionsName = "AzureAd"
  }
}.WithAgentUserIdentity(agentIdentity.AgenticAppId, agentUserId);

string authorizationHeader = await _authorizationHeaderProvider
  .CreateAuthorizationHeaderForUserAsync(
    Scopes,
    options,
    new ClaimsPrincipal(),
    cancellationToken);
```

The Graph scope is `https://graph.microsoft.com/.default`, which requests the permissions already consented for the agent identity and agent user. Missing recipient identity information causes an error; the service never falls back to an app-only token.

The Graph client uses `AnonymousAuthenticationProvider` because authentication is supplied explicitly on each request, not because the call is anonymous. The request-local header avoids sharing one agent user's token with another conversation. The service requests only the profile properties used by the response:

```csharp
var user = await _graphClient.Users[userId].GetAsync(
  request =>
  {
    request.Headers.Add("Authorization", authorizationHeader);
    request.QueryParameters.Select =
      ["displayName", "mail", "userPrincipalName"];
  },
    cancellationToken);
```

`userId` is the human sender whose profile is being read. `agentIdentity.AgenticUserId` is the agent user authenticating the call. Calling `/me` with this token would return the agent user's profile, not the sender's.

The deliberate delay in `GraphAgentOrchestrator.cs` makes streaming visible during the lab. Remove it from production code.

## Step 2: Grant Graph access to the agent user

Use the existing agent blueprint, agent identity, and linked agent user. Do not create a separate Graph app or grant `User.Read.All` application permission for this lab.

1. Record the blueprint application ID, tenant ID, and blueprint secret **Value**. These are the credentials used in Step 3. The secret belongs to the blueprint, not the agent identity or agent user.
2. Record the agent identity's application (client) ID and its linked agent user's object ID from the digital worker setup. These are distinct from the blueprint application ID and the human sender's object ID.
3. Open [Microsoft Graph Explorer](https://developer.microsoft.com/en-us/graph/graph-explorer) and sign in to the same development tenant as an administrator authorized to grant delegated permissions, such as a **Privileged Role Administrator**. Grant Graph Explorer the delegated `Application.Read.All` and `DelegatedPermissionGrant.ReadWrite.All` permissions for this setup operation. See the [Graph Explorer setup guide](../../dependencies/graph-explorer/Readme.md).
4. Look up the agent identity's service principal **object ID** and the Microsoft Graph service principal **object ID** with these requests. Copy `value[0].id` from each result:

```http
GET https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '<agent-identity-client-id>'&$select=id,appId,displayName
```

```http
GET https://graph.microsoft.com/v1.0/servicePrincipals?$filter=appId eq '00000003-0000-0000-c000-000000000000'&$select=id,appId,displayName
```

5. Check for an existing grant before creating one:

```http
GET https://graph.microsoft.com/v1.0/oauth2PermissionGrants?$filter=clientId eq '<agent-identity-service-principal-object-id>'
```

If no grant covers this agent user and Microsoft Graph, create the following grant. In Graph Explorer, select **POST**, set the URL, and enter the JSON request body:

```http
POST https://graph.microsoft.com/v1.0/oauth2PermissionGrants
Content-Type: application/json
```

```json
{
  "clientId": "<agent-identity-service-principal-object-id>",
  "consentType": "Principal",
  "principalId": "<agent-user-object-id>",
  "resourceId": "<microsoft-graph-service-principal-object-id>",
  "scope": "User.ReadBasic.All"
}
```

For an existing `Principal` grant with the same `principalId` and `resourceId`, use `PATCH /v1.0/oauth2PermissionGrants/{grant-id}` to add `User.ReadBasic.All` to its space-separated `scope` value, preserving all existing scopes. If an existing `AllPrincipals` grant already includes that permission for Graph, no additional grant is needed.

`User.ReadBasic.All` is a **delegated** permission that lets the agent user read other users' basic profiles, including the three properties used here. `User.Read` alone would only allow the agent user's own profile. The grant's `clientId` is the agent identity's service principal object ID, not the blueprint ID or its application ID. `consentType: Principal` limits this grant to the linked agent user.

Use a dedicated development tenant. Graph Explorer's setup permissions are for the administrator performing consent; they are not runtime permissions for the agent. Do not delete other grants used by your digital worker.

## Step 3: Configure appsettings.json

Open `code/appsettings.json` and replace the placeholders in `AzureAd`.

Configure `AzureAd` with the agent blueprint identity used for messaging and agent-user token acquisition:

```json
"AzureAd": {
  "ClientId": "<agent-blueprint-id>",
  "TenantId": "<tenant-id>",
  "ClientCredentials": [
    {
      "SourceType": "ClientSecret",
      "ClientSecret": "<agent-blueprint-client-secret>"
    }
  ]
}
```

There is no separate `Graph` credential section. The Teams SDK reads `AgenticAppId` and `AgenticUserId` from the incoming activity's **recipient**, so you do not hardcode an agent instance in configuration. Ensure that the recipient is the agent user linked to the agent identity authorized in Step 2 and that it belongs to this blueprint and tenant.

Do not commit a populated `appsettings.json` containing client secrets. Client secrets are for local lab use only; use certificates or managed-identity federation for production.

## Step 4: Build and run the agent locally

From `labs/lab-05.1-generating-and-using-agentic-user-token/code`, run:

```powershell
dotnet build
dotnet run --no-build
```

The application listens on `http://localhost:3978`. Keep this terminal running.

Stop any other lab application using port 3978 before starting this one. The labs are separate applications and should be run one at a time with the sample configuration.

## Step 5: Set up the dev tunnel

Make sure [Microsoft dev tunnel](../../dependencies/dev-tunnel/Readme.md) is installed and authenticated. In a second terminal, run:

```powershell
devtunnel host -p 3978 --allow-anonymous
```

Use the returned host to form the notification endpoint:

```text
https://domain.devtunnels.ms/api/messages
```

Keep the tunnel running while you test the agent.

## Step 6: Configure the callback URL

Open the [Microsoft 365 Developer Portal](https://dev.teams.microsoft.com/tools/agent-blueprint) and select the blueprint whose application ID matches `AzureAd:ClientId`.

Under **Configuration > Notification Configuration**:

1. Set **Agent Type** to **API Based**.
2. Set **Notification Url** to your dev tunnel `/api/messages` endpoint.
3. Save the configuration.

## Step 7: Test the Graph call

1. Open [Microsoft Teams](https://teams.microsoft.com).
2. Find the agentic user and open a chat.
3. Send any message.
4. Confirm that **Fetching your profile from Microsoft Graph** appears as an informative update.
5. Confirm that the final response streams your display name and email address.

The displayed profile is still yours, but the Graph caller is the agent user. To verify the identity during development, inspect the token only in a trusted local debugger: its `oid` should match the recipient's `AgenticUserId`, and its `scp` should include `User.ReadBasic.All`. Do not log, share, or paste access tokens into external tools. If Graph returns an opaque token, do not depend on decoding it; use the tenant's sign-in logs to investigate instead.

## Troubleshooting

- **Missing configuration value**: Populate every placeholder in `AzureAd`. A separate Graph client ID or secret is not used.
- **401 Unauthorized or token exchange failure**: Verify the blueprint tenant ID, application ID, and client secret. Confirm that `ClientSecret` contains the secret value, not its ID, and that the agent identity belongs to this blueprint and the agent user is linked to that identity.
- **Consent required or 403 Forbidden**: Verify the delegated `User.ReadBasic.All` grant for the agent identity and agent user in Step 2. An application permission on a separate app or the blueprint does not authorize this flow. After changing consent, restart the local application to clear its local token cache and retry.
- **Recipient agent identity missing or invalid**: Send a real Teams message to the provisioned agentic user and confirm that `Recipient.GetAgenticIdentity()` includes `AgenticAppId` and a valid `AgenticUserId` GUID. Do not substitute the sender ID or fall back to app-only authentication.
- **Sender object ID missing**: Test from a Microsoft 365 user chat in the same tenant and confirm the incoming activity contains `From.AadObjectId`.
- **User not found**: Confirm that the sender exists in the tenant configured under `AzureAd:TenantId`.
- **Wrong profile when using `/me`**: `/me` represents the agent user with this token. This lab intentionally calls `/users/{sender-id}` to retrieve the human sender's profile.
- **Email fallback**: If the user's `mail` property is empty, the sample displays `userPrincipalName` instead.

For more information, see the [agent-user authentication flow and SDK example](https://learn.microsoft.com/en-us/entra/agent-id/autonomous-agent-authentication-authorization-flow#request-an-agents-user-account-token), [Microsoft Identity Web agent identities documentation](https://learn.microsoft.com/en-us/entra/msidweb/call-downstream-apis/agent-identities), [delegated permission grant API](https://learn.microsoft.com/en-us/graph/api/oauth2permissiongrant-post), and [Get user API documentation](https://learn.microsoft.com/en-us/graph/api/user-get).
