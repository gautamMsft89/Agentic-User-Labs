# Lab 12 - Azure OpenAI Orchestrator

## Overview

This work-in-progress lab connects an API-based Teams agent to an Azure OpenAI deployment. The agent streams model responses to Teams and makes the Microsoft Learn MCP tools available to the model. Before a response starts, it can show informative updates such as "Thinking..." or the name of a tool being called.

You will need an agent blueprint and its Microsoft Entra ID credentials, the .NET 10 SDK, a reachable Azure OpenAI deployment with an API key, and Microsoft dev tunnel.

## Step 1: Get the lab code sample

Clone this repository, then open `labs/lab-12-azure-openai-orchestrator/code`.

- `Program.cs` receives Teams messages and streams updates back to the conversation.
- `OpenAiAgenticOrchestrator.cs` calls the model and exposes the Microsoft Learn MCP tools.
- `IAgentOrchestrator.cs` defines the update contract.
- `appsettings.json` contains local configuration placeholders.

## Step 2: Configure the agent

In `code/appsettings.json`, fill in `AzureAd:ClientId`, `AzureAd:TenantId`, and `AzureAd:ClientCredentials[0]:ClientSecret` with your agent blueprint credentials. Set `MODEL` to your Azure OpenAI deployment name, `AZURE_OPENAI_BASE_URL` to its OpenAI-compatible API base URL (for example, `https://<resource>.openai.azure.com/openai/v1/`), and `AZURE_OPENAI_API_KEY` to its API key.

The sample settings file is ignored by Git. Keep credentials out of commits and use environment variables or another secure configuration source when sharing or deploying the sample.

## Step 3: Build and run locally

From `labs/lab-12-azure-openai-orchestrator/code`, run:

```powershell
dotnet build
dotnet run --no-build
```

The application listens on `http://localhost:3978`. It connects to the Microsoft Learn MCP endpoint during startup, so it needs network access to `https://learn.microsoft.com/api/mcp`.

## Step 4: Set up the dev tunnel

In a second terminal, run:

```powershell
devtunnel host -p 3978 --allow-anonymous
```

Use the resulting `https://<your-tunnel-domain>/api/messages` URL as the notification endpoint.

## Step 5: Configure the callback URL

In the [Microsoft 365 Developer Portal](https://dev.teams.microsoft.com/tools/agent-blueprint), select the matching blueprint and open **Configuration > Notification Configuration**. Set **Agent Type** to **API Based**, set **Notification Url** to the dev tunnel endpoint, and save.

## Step 6: Test the response

Send the agent a question in Teams. Confirm it displays an informative update, then streams a response. Ask a question about Microsoft Learn documentation to give the model an opportunity to call a tool; tool calls appear as informative updates before response streaming begins.

## Troubleshooting

- **Startup reports missing configuration**: Check `MODEL`, `AZURE_OPENAI_BASE_URL`, and `AZURE_OPENAI_API_KEY`.
- **Startup cannot list tools**: Check access to the Microsoft Learn MCP endpoint.
- **No Teams response arrives**: Check the blueprint credentials, the running application and tunnel, and the notification URL.