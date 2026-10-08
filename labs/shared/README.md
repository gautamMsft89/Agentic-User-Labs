# Required infrastructure for the WorkIQ lab

`AgenticLabs.Common\AgenticLabs.Common.csproj` is a .NET 10 library referenced by
[Lab13 WorkIQ](../lab-13-workiq-files/README.md). This selected publication includes only the
shared source needed by that host and its offline fixtures, not separate Teams MCP/Graph SDK labs.

It owns Teams bot delivery/progress, trusted activity and audience models, bounded model transport,
schema/result/image helpers, private addressing and safe diagnostic formatting. Actual WorkIQ MCP
sessions, AU/human token acquisition, OAuth connections, private workers and WorkIQ skill resources
remain in Lab13. There is no second runnable host hidden in this library.

The Teams bot SDK is necessary for replies; it is not a Teams MCP data provider. Identity-support
dependencies and Graph-shaped resource metadata are not direct Graph data workflows.
The original mixed common library's Graph command parser/renderer, Graph budget override,
native Teams MCP contract and foreign-host friend assemblies are excluded. `FileCommand` is retained
only as a compatibility name for shared ID/name/metadata helpers and exceptions, not a slash parser.
Ordinary WorkIQ model budgets remain the configured native budgets.

Provider-isolation checks retain legacy setting names to reject foreign activation with migration
guidance; they cannot install another provider. Some compatibility policy fields remain because
authorization snapshots and private policy fingerprints use the schema. No credential/session cache
was made static or shared between processes.

Local secrets and populated settings are not published. Start from the Lab13 placeholder sample
only for a new setup; never overwrite existing settings. See its current README for credential roles,
fresh-terminal setup, effective AU defaults, explicit human opt-in and model compatibility guidance.

From the repository root:

```powershell
dotnet build .\labs\shared\AgenticLabs.Common\AgenticLabs.Common.csproj -c Release
dotnet build .\labs\lab-13-workiq-files\code\WorkIqFiles.csproj -c Release
dotnet run --project .\labs\lab-13-workiq-files\validation\WorkIqFiles.Validation.csproj -c Release --no-launch-profile
```

The validation runner is self-contained and synthetic; it neither starts the bot nor calls live
Microsoft 365/model services. Detailed scope and measured publication counts are in
[Lab13 TESTING](../lab-13-workiq-files/TESTING.md). Historical mixed-provider test totals are not
claimed for this selected tree.
