using Calendar.Application.Auth;
using Calendar.Domain;
using Calendar.Infrastructure.Auth;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Infrastructure.Tests;

/// <summary>
/// Broker behavior for the non-OAuth schemes (SDK-CONTRACT.md §3, PLUGIN-HOST.md §6.2): ApiKey header/query
/// injection, Basic / AppPassword headers, and the never-leak-secret guarantee.
/// </summary>
public sealed class AuthBrokerSchemeTests
{
    private static readonly Guid Account = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static string Aad => $"account:{Account:N}";

    private static AuthBroker Broker(CredentialContext ctx, ITokenVault vault) =>
        new(ctx, vault, () => throw new InvalidOperationException("no HTTP for non-OAuth schemes"),
            NullLogger.Instance);

    [Fact]
    public async Task ApiKey_is_injected_as_a_header_with_the_format_template()
    {
        var vault = new InMemoryTokenVault();
        var secret = vault.Seed(SecretKind.ApiKey, "SECRET-KEY", Aad);
        var ctx = new CredentialContext(Account,
            new AuthSpec(AuthScheme.ApiKey, In: "header", Name: "Authorization", Format: "Bearer {token}"),
            SecretRef: secret);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/v1/data");
        await Broker(ctx, vault).ApplyAsync(request, null, CancellationToken.None);

        Assert.Equal("Bearer SECRET-KEY", request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task ApiKey_with_a_custom_header_name_uses_that_header()
    {
        var vault = new InMemoryTokenVault();
        var secret = vault.Seed(SecretKind.ApiKey, "abc123", Aad);
        var ctx = new CredentialContext(Account,
            new AuthSpec(AuthScheme.ApiKey, In: "header", Name: "X-Goog-Api-Key", Format: null),
            SecretRef: secret);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://maps.example.test/geocode");
        await Broker(ctx, vault).ApplyAsync(request, null, CancellationToken.None);

        Assert.Equal("abc123", request.Headers.GetValues("X-Goog-Api-Key").Single());
        Assert.False(request.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task ApiKey_is_injected_as_a_query_parameter_when_In_is_query()
    {
        var vault = new InMemoryTokenVault();
        var secret = vault.Seed(SecretKind.ApiKey, "k e y/with+chars", Aad);
        var ctx = new CredentialContext(Account,
            new AuthSpec(AuthScheme.ApiKey, In: "query", Name: "apikey", Format: null),
            SecretRef: secret);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/search?q=berlin");
        await Broker(ctx, vault).ApplyAsync(request, null, CancellationToken.None);

        // Existing query is preserved and the key is URL-encoded.
        Assert.Contains("q=berlin", request.RequestUri!.Query);
        Assert.Contains("apikey=k%20e%20y%2Fwith%2Bchars", request.RequestUri!.Query);
    }

    [Fact]
    public async Task Basic_scheme_writes_an_Authorization_Basic_header()
    {
        var vault = new InMemoryTokenVault();
        var secret = vault.Seed(SecretKind.BasicPassword, "hunter2", Aad);
        var ctx = new CredentialContext(Account,
            new AuthSpec(AuthScheme.Basic), SecretRef: secret, Username: "alice");

        var request = new HttpRequestMessage(HttpMethod.Get, "https://dav.example.test/");
        await Broker(ctx, vault).ApplyAsync(request, null, CancellationToken.None);

        var header = request.Headers.Authorization!;
        Assert.Equal("Basic", header.Scheme);
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!));
        Assert.Equal("alice:hunter2", decoded);
    }

    [Fact]
    public async Task AppPassword_scheme_uses_the_app_specific_password_over_Basic()
    {
        var vault = new InMemoryTokenVault();
        var secret = vault.Seed(SecretKind.AppPassword, "abcd-efgh-ijkl-mnop", Aad);
        var ctx = new CredentialContext(Account,
            new AuthSpec(AuthScheme.AppPassword), SecretRef: secret, Username: "user@icloud.com");

        var request = new HttpRequestMessage(HttpMethod.Get, "https://caldav.icloud.com/");
        await Broker(ctx, vault).ApplyAsync(request, null, CancellationToken.None);

        var decoded = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(request.Headers.Authorization!.Parameter!));
        Assert.Equal("user@icloud.com:abcd-efgh-ijkl-mnop", decoded);
    }

    [Fact]
    public async Task GetTokenAsync_for_Basic_never_exposes_the_token_field()
    {
        var vault = new InMemoryTokenVault();
        var secret = vault.Seed(SecretKind.BasicPassword, "hunter2", Aad);
        var ctx = new CredentialContext(Account,
            new AuthSpec(AuthScheme.Basic), SecretRef: secret, Username: "alice");

        var handle = await Broker(ctx, vault).GetTokenAsync(null, CancellationToken.None);

        // Basic carries username/password — never an opaque bearer Token.
        Assert.Null(handle.Token);
        Assert.Equal("alice", handle.Username);
        Assert.Equal("hunter2", handle.Password);
    }

    [Fact]
    public async Task None_scheme_yields_the_empty_handle_and_touches_no_request()
    {
        var ctx = new CredentialContext(Account, new AuthSpec(AuthScheme.None));
        var broker = Broker(ctx, new InMemoryTokenVault());

        var handle = await broker.GetTokenAsync(null, CancellationToken.None);
        Assert.Same(AuthHandle.Empty, handle);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://feed.example.test/cal.ics");
        await broker.ApplyAsync(request, null, CancellationToken.None);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task A_credential_sealed_for_another_account_cannot_be_read()
    {
        var vault = new InMemoryTokenVault();
        // Sealed under a DIFFERENT account's AAD.
        var foreignAad = $"account:{Guid.NewGuid():N}";
        var secret = vault.Seed(SecretKind.ApiKey, "SECRET", foreignAad);
        var ctx = new CredentialContext(Account,
            new AuthSpec(AuthScheme.ApiKey, In: "header", Name: "Authorization"), SecretRef: secret);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Broker(ctx, vault).ApplyAsync(request, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_requested_scope_outside_the_manifest_is_rejected()
    {
        var vault = new InMemoryTokenVault();
        var secret = vault.Seed(SecretKind.ApiKey, "k", Aad);
        var ctx = new CredentialContext(Account,
            new AuthSpec(AuthScheme.ApiKey, Name: "Authorization", Scopes: new[] { "read" }),
            SecretRef: secret);

        await Assert.ThrowsAsync<AuthBrokerException>(
            () => Broker(ctx, vault).GetTokenAsync(new[] { "write" }, CancellationToken.None));
    }
}
