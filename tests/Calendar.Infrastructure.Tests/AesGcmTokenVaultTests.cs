using System.Security.Cryptography;
using Calendar.Domain;
using Calendar.Infrastructure.Auth;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Calendar.Infrastructure.Tests;

/// <summary>
/// The AES-256-GCM token vault over a real temp SQLite file (PLUGIN-HOST.md §6.4, DATA-SCHEMA §2.2): round-
/// trip, AAD binding, tamper detection, in-place rotation, and the at-rest invariant that no plaintext is
/// stored.
/// </summary>
public sealed class AesGcmTokenVaultTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-vault-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _provider;

    private CalendarDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    /// <summary>A real scope factory over the same SQLite file — the vault opens a fresh context per op.</summary>
    private IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

    private static IVaultKeyProvider Keys() =>
        new ConfigurationVaultKeyProvider(Options.Create(new VaultKeyOptions
        {
            // A fixed 32-byte key so a second context (same DB) can decrypt.
            MasterKeyBase64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
            KeyId = "test-key",
        }));

    public AesGcmTokenVaultTests()
    {
        using var ctx = NewContext();
        ctx.Database.Migrate();

        var services = new ServiceCollection();
        services.AddDbContext<CalendarDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Store_then_read_round_trips_the_plaintext()
    {
        Guid id;
        using (var ctx = NewContext())
        {
            var vault = new AesGcmTokenVault(ScopeFactory, Keys());
            id = await vault.StoreAsync(SecretKind.OAuthRefreshToken, "RT-secret", "account:abc", null, CancellationToken.None);
        }

        using (var ctx = NewContext())
        {
            var vault = new AesGcmTokenVault(ScopeFactory, Keys());
            var entry = await vault.ReadAsync(id, "account:abc", CancellationToken.None);
            Assert.Equal("RT-secret", entry!.Value);
            Assert.Equal(SecretKind.OAuthRefreshToken, entry.Kind);
        }
    }

    [Fact]
    public async Task The_plaintext_is_never_persisted_in_the_row()
    {
        using var ctx = NewContext();
        var vault = new AesGcmTokenVault(ScopeFactory, Keys());
        var id = await vault.StoreAsync(SecretKind.ApiKey, "PLAINTEXT-SECRET", "aad", null, CancellationToken.None);

        ctx.ChangeTracker.Clear();
        var row = ctx.Secrets.Single(s => s.Id == id);

        Assert.Equal("AES-256-GCM", row.Algorithm);
        Assert.Equal(12, row.Nonce.Length);
        Assert.Equal(16, row.AuthTag!.Length);
        // The ciphertext bytes must not contain the plaintext.
        var cipherText = System.Text.Encoding.UTF8.GetString(row.Ciphertext);
        Assert.DoesNotContain("PLAINTEXT-SECRET", cipherText);
        Assert.DoesNotContain("PLAINTEXT-SECRET", System.Text.Encoding.ASCII.GetString(row.Ciphertext));
    }

    [Fact]
    public async Task Reading_with_the_wrong_aad_fails_the_auth_tag()
    {
        using var ctx = NewContext();
        var vault = new AesGcmTokenVault(ScopeFactory, Keys());
        var id = await vault.StoreAsync(SecretKind.ApiKey, "k", "account:right", null, CancellationToken.None);

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => vault.ReadAsync(id, "account:wrong", CancellationToken.None));
    }

    [Fact]
    public async Task A_tampered_ciphertext_is_rejected()
    {
        Guid id;
        using (var ctx = NewContext())
        {
            var vault = new AesGcmTokenVault(ScopeFactory, Keys());
            id = await vault.StoreAsync(SecretKind.ApiKey, "k", "aad", null, CancellationToken.None);
        }

        using (var ctx = NewContext())
        {
            var row = ctx.Secrets.Single(s => s.Id == id);
            var tampered = (byte[])row.Ciphertext.Clone();
            tampered[0] ^= 0xFF;          // flip a bit
            row.Ciphertext = tampered;     // reassign so EF tracks the change
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var vault = new AesGcmTokenVault(ScopeFactory, Keys());
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
                () => vault.ReadAsync(id, "aad", CancellationToken.None));
        }
    }

    [Fact]
    public async Task Update_rotates_the_value_under_a_stable_handle()
    {
        using var ctx = NewContext();
        var vault = new AesGcmTokenVault(ScopeFactory, Keys());
        var id = await vault.StoreAsync(SecretKind.OAuthRefreshToken, "RT-OLD", "aad", null, CancellationToken.None);

        await vault.UpdateAsync(id, SecretKind.OAuthRefreshToken, "RT-NEW", "aad", null, CancellationToken.None);

        var entry = await vault.ReadAsync(id, "aad", CancellationToken.None);
        Assert.Equal("RT-NEW", entry!.Value);
    }

    [Fact]
    public async Task Each_write_uses_a_fresh_nonce()
    {
        using var ctx = NewContext();
        var vault = new AesGcmTokenVault(ScopeFactory, Keys());
        var a = await vault.StoreAsync(SecretKind.ApiKey, "same", "aad", null, CancellationToken.None);
        var b = await vault.StoreAsync(SecretKind.ApiKey, "same", "aad", null, CancellationToken.None);

        ctx.ChangeTracker.Clear();
        var rowA = ctx.Secrets.Single(s => s.Id == a);
        var rowB = ctx.Secrets.Single(s => s.Id == b);
        Assert.NotEqual(rowA.Nonce, rowB.Nonce);
        Assert.NotEqual(rowA.Ciphertext, rowB.Ciphertext); // same plaintext, different ciphertext
    }

    [Fact]
    public async Task Remove_purges_the_entry()
    {
        using var ctx = NewContext();
        var vault = new AesGcmTokenVault(ScopeFactory, Keys());
        var id = await vault.StoreAsync(SecretKind.ApiKey, "k", "aad", null, CancellationToken.None);

        await vault.RemoveAsync(id, CancellationToken.None);

        Assert.Null(await vault.ReadAsync(id, "aad", CancellationToken.None));
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
