using System.Text;
using Cursivis.Infrastructure.Storage.Security;
using Cursivis.IntegrationTests.TestSupport;
using Cursivis.Windows.Platform.Security;

namespace Cursivis.IntegrationTests.Windows;

public sealed class WindowsCurrentUserSecretStoreTests
{
    private const string FakeSecret = "integration-test-only-token";

    [Fact]
    public async Task SaveReadDelete_RoundTripsThroughCurrentUserDpapiWithoutPlaintextAtRest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var store = new WindowsCurrentUserSecretStore(
            new WindowsCurrentUserSecretStoreOptions(
                temporary.Path,
                "Cursivis.IntegrationTests/v1"));
        using var input = new SecretBuffer(FakeSecret);

        await store.SaveAsync("openai-api-key", input);

        Assert.True(store.Exists("openai-api-key"));
        var encryptedPath = Assert.Single(Directory.EnumerateFiles(temporary.Path, "*.bin"));
        var encrypted = await File.ReadAllBytesAsync(encryptedPath);
        var plaintext = Encoding.UTF8.GetBytes(FakeSecret);
        try
        {
            Assert.Equal(-1, encrypted.AsSpan().IndexOf(plaintext));
        }
        finally
        {
            Array.Clear(plaintext);
            Array.Clear(encrypted);
        }

        using var restored = await store.ReadAsync("openai-api-key");
        Assert.NotNull(restored);
        Assert.True(restored.Use(SecretMatches));
        Assert.Equal("[REDACTED]", restored.ToString());

        Assert.True(await store.DeleteAsync("openai-api-key"));
        Assert.False(store.Exists("openai-api-key"));
        Assert.Null(await store.ReadAsync("openai-api-key"));
        Assert.False(await store.DeleteAsync("openai-api-key"));
    }

    [Fact]
    public async Task Read_TamperedCiphertext_FailsClosedWithSanitizedErrorCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var store = new WindowsCurrentUserSecretStore(
            new WindowsCurrentUserSecretStoreOptions(
                temporary.Path,
                "Cursivis.IntegrationTests/v1"));
        using var input = new SecretBuffer(FakeSecret);
        await store.SaveAsync("openai-api-key", input);

        var encryptedPath = Assert.Single(Directory.EnumerateFiles(temporary.Path, "*.bin"));
        var payload = await File.ReadAllBytesAsync(encryptedPath);
        payload[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(encryptedPath, payload);
        Array.Clear(payload);

        var exception = await Assert.ThrowsAsync<SecretStoreException>(() =>
            store.ReadAsync("openai-api-key"));

        Assert.Equal(SecretStoreFailureCode.DecryptionFailed, exception.FailureCode);
        Assert.DoesNotContain(FakeSecret, exception.Message, StringComparison.Ordinal);
    }

    private static bool SecretMatches(ReadOnlySpan<char> value) =>
        value.SequenceEqual(FakeSecret.AsSpan());
}
