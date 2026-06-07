using System.Net;
using Calendar.Application.Auth;
using Calendar.Domain;
using Calendar.Infrastructure.Auth;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Infrastructure.Tests;

/// <summary>
/// OAuth flows against a STUB token endpoint (no live network): PKCE authorization-code exchange, silent
/// refresh (incl. rotated refresh token re-vaulting), client-credentials grant, and the guarantee that the
/// broker never surfaces a client secret or refresh token (SDK-CONTRACT.md §3, PLUGIN-HOST.md §6.3).
/// </summary>
public sealed class AuthBrokerOAuthTests
{
    private static readonly Guid Account = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static string Aad => $"account:{Account:N}";

    private static OAuthClient PublicClient => new("client-123", ClientSecret: null, "https://app.test/callback");

    private static AuthSpec PkceSpec => new(
        AuthScheme.OAuth2Pkce,
        AuthorizationUrl: "https://idp.test/authorize",
        TokenUrl: "https://idp.test/token",
        Scopes: new[] { "calendar.read" },
        Params: new Dictionary<string, string> { ["access_type"] = "offline" });

    // --- PKCE connect flow ---

    [Fact]
    public async Task Pkce_connect_exchanges_the_code_and_vaults_the_refresh_token()
    {
        var vault = new InMemoryTokenVault();
        var stub = StubHttpMessageHandler.Json(
            """{"access_token":"AT-1","refresh_token":"RT-1","expires_in":3600,"token_type":"Bearer"}""");
        var flow = NewFlow(vault, stub);

        var ctx = new CredentialContext(Account, PkceSpec, PublicClient);
        var challenge = flow.BeginAuthorization(ctx, scopes: null);

        // The authorization URL carries PKCE S256 + state + manifest extra params; NO secret.
        Assert.Contains("code_challenge_method=S256", challenge.RedirectUrl);
        Assert.Contains("code_challenge=", challenge.RedirectUrl);
        Assert.Contains($"state={Uri.EscapeDataString(challenge.State)}", challenge.RedirectUrl);
        Assert.Contains("access_type=offline", challenge.RedirectUrl);
        Assert.DoesNotContain("client_secret", challenge.RedirectUrl);

        var result = await flow.CompleteAuthorizationAsync(challenge.State, "auth-code-xyz", CancellationToken.None);

        // The token endpoint saw an authorization_code grant carrying the code_verifier (PKCE proof).
        var exchange = Assert.Single(stub.Requests);
        Assert.Contains("grant_type=authorization_code", exchange.Body);
        Assert.Contains("code=auth-code-xyz", exchange.Body);
        Assert.Contains("code_verifier=", exchange.Body);

        // The refresh token is sealed in the vault under the account's AAD; the access token is NOT persisted.
        var entry = await vault.ReadAsync(result.SecretRef, Aad, CancellationToken.None);
        Assert.Equal("RT-1", entry!.Value);
        Assert.Equal(SecretKind.OAuthRefreshToken, entry.Kind);
    }

    [Fact]
    public async Task Pkce_connect_rejects_a_replayed_or_unknown_state()
    {
        var flow = NewFlow(new InMemoryTokenVault(), StubHttpMessageHandler.Json("{}"));
        await Assert.ThrowsAsync<AuthBrokerException>(
            () => flow.CompleteAuthorizationAsync("never-issued", "code", CancellationToken.None));
    }

    // --- silent refresh ---

    [Fact]
    public async Task GetToken_refreshes_silently_and_returns_only_a_short_lived_bearer()
    {
        var vault = new InMemoryTokenVault();
        var secret = vault.Seed(SecretKind.OAuthRefreshToken, "RT-1", Aad);
        var stub = StubHttpMessageHandler.Json(
            """{"access_token":"AT-FRESH","expires_in":3600,"token_type":"Bearer"}""");
        var broker = NewBroker(vault, stub, new CredentialContext(Account, PkceSpec, PublicClient, secret));

        var handle = await broker.GetTokenAsync(new[] { "calendar.read" }, CancellationToken.None);

        Assert.Equal(AuthScheme.OAuth2Pkce, handle.Scheme);
        Assert.Equal("AT-FRESH", handle.Token);          // a freshly minted bearer
        Assert.NotNull(handle.ExpiresAt);                // scoped, short-lived
        Assert.Null(handle.Password);                    // no Basic material

        var refresh = Assert.Single(stub.Requests);
        Assert.Contains("grant_type=refresh_token", refresh.Body);
        Assert.Contains("refresh_token=RT-1", refresh.Body);
    }

    [Fact]
    public async Task A_cached_bearer_is_reused_without_a_second_token_call()
    {
        var vault = new InMemoryTokenVault();
        var secret = vault.Seed(SecretKind.OAuthRefreshToken, "RT-1", Aad);
        var stub = StubHttpMessageHandler.Json(
            """{"access_token":"AT-1","expires_in":3600,"token_type":"Bearer"}""");
        var broker = NewBroker(vault, stub, new CredentialContext(Account, PkceSpec, PublicClient, secret));

        await broker.ApplyAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/"), null, CancellationToken.None);
        await broker.ApplyAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/"), null, CancellationToken.None);

        Assert.Single(stub.Requests); // one refresh covers both applies
    }

    [Fact]
    public async Task A_rotated_refresh_token_is_re_vaulted_under_the_same_handle()
    {
        var vault = new InMemoryTokenVault();
        var secret = vault.Seed(SecretKind.OAuthRefreshToken, "RT-OLD", Aad);
        var stub = StubHttpMessageHandler.Json(
            """{"access_token":"AT-2","refresh_token":"RT-NEW","expires_in":3600}""");
        var broker = NewBroker(vault, stub, new CredentialContext(Account, PkceSpec, PublicClient, secret));

        await broker.GetTokenAsync(null, CancellationToken.None);

        Assert.Equal("RT-NEW", vault.Raw(secret)); // handle id stable, value rotated
    }

    // --- client credentials ---

    [Fact]
    public async Task Client_credentials_grant_mints_a_bearer_without_a_user()
    {
        var spec = new AuthSpec(AuthScheme.OAuth2ClientCredentials, TokenUrl: "https://idp.test/token",
            Scopes: new[] { "notify" });
        var confidential = new OAuthClient("svc-client", "svc-secret", "https://app.test/callback");
        var stub = StubHttpMessageHandler.Json(
            """{"access_token":"AT-CC","expires_in":1800,"token_type":"Bearer"}""");
        var broker = NewBroker(new InMemoryTokenVault(), stub,
            new CredentialContext(Account, spec, confidential));

        var handle = await broker.GetTokenAsync(null, CancellationToken.None);

        Assert.Equal("AT-CC", handle.Token);
        var req = Assert.Single(stub.Requests);
        Assert.Contains("grant_type=client_credentials", req.Body);
        Assert.Contains("client_id=svc-client", req.Body);
        // The confidential secret travels host→IdP only; it must never reach the plugin's handle.
        Assert.Contains("client_secret=svc-secret", req.Body);
        Assert.DoesNotContain("svc-secret", handle.Token);
    }

    [Fact]
    public async Task A_failed_token_response_surfaces_as_an_AuthBrokerException()
    {
        var vault = new InMemoryTokenVault();
        var secret = vault.Seed(SecretKind.OAuthRefreshToken, "RT-REVOKED", Aad);
        var stub = StubHttpMessageHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        var broker = NewBroker(vault, stub, new CredentialContext(Account, PkceSpec, PublicClient, secret));

        await Assert.ThrowsAsync<AuthBrokerException>(
            () => broker.GetTokenAsync(null, CancellationToken.None));
    }

    // --- helpers ---

    private static AuthBroker NewBroker(ITokenVault vault, StubHttpMessageHandler stub, CredentialContext ctx)
    {
        var client = new HttpClient(stub);
        return new AuthBroker(ctx, vault, () => client, NullLogger.Instance);
    }

    private static OAuthFlowService NewFlow(ITokenVault vault, StubHttpMessageHandler stub)
    {
        var client = new HttpClient(stub);
        return new OAuthFlowService(vault, () => client, NullLogger<OAuthFlowService>.Instance);
    }
}
