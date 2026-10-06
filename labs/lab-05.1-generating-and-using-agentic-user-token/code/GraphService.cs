using System.Security.Claims;
using Microsoft.Graph;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Teams.Core.Schema;

namespace GraphApiStreamingApp;

internal sealed class GraphService
{
    private static readonly string[] Scopes =
        ["https://graph.microsoft.com/.default"];

    private readonly IAuthorizationHeaderProvider _authorizationHeaderProvider;
    private readonly GraphServiceClient _graphClient =
        new(new AnonymousAuthenticationProvider());

    public GraphService(IAuthorizationHeaderProvider authorizationHeaderProvider) =>
        _authorizationHeaderProvider = authorizationHeaderProvider;

    internal async Task<UserProfile> GetUserProfileAsync(
        string userId,
        AgenticIdentity? agentIdentity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException(
                "The incoming activity does not contain a Microsoft Entra object ID for the sender.");
        }

        if (agentIdentity is null
            || string.IsNullOrWhiteSpace(agentIdentity.AgenticAppId)
            || !Guid.TryParse(agentIdentity.AgenticUserId, out Guid agentUserId)
            || agentUserId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "The incoming activity recipient does not contain a valid agent identity and agent user object ID.");
        }

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

        var user = await _graphClient.Users[userId].GetAsync(
            request =>
            {
                request.Headers.Add("Authorization", authorizationHeader);
                request.QueryParameters.Select =
                    ["displayName", "mail", "userPrincipalName"];
            },
            cancellationToken);

        string displayName = user?.DisplayName
            ?? user?.UserPrincipalName
            ?? "there";
        string email = user?.Mail
            ?? user?.UserPrincipalName
            ?? "your inbox";

        return new UserProfile(displayName, email);
    }
}

internal sealed record UserProfile(string DisplayName, string Email);
