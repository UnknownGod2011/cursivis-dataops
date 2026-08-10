using Cursivis.Infrastructure.Storage.Persistence;
using Cursivis.Infrastructure.Storage.Security;

namespace Cursivis.Windows.Platform.Security;

/// <summary>
/// Keeps the hackathon Gemini credential separate from the inherited optional
/// OpenAI voice/realtime credential. Secrets are protected with the same
/// current-user Windows store Cursivis already uses and are copied into the
/// current process only so the provider can consume them without persisting
/// plaintext configuration.
/// </summary>
public static class WindowsGeminiCredentialStore
{
    public const string LogicalSecretName = "gemini-api-key";
    private const string ApplicationPurpose = "Cursivis.Gemini.ApiKey.v1";

    public static WindowsGeminiCredentialManager CreateManager()
    {
        CursivisStoragePaths paths = CursivisStoragePaths.ForCurrentUser();
        var store = new WindowsCurrentUserSecretStore(
            new WindowsCurrentUserSecretStoreOptions(paths.SecretsDirectory, ApplicationPurpose));
        return new WindowsGeminiCredentialManager(store);
    }

    public static async Task<bool> LoadIntoProcessEnvironmentAsync(
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY")))
        {
            EnsureDataHubDefault();
            return true;
        }

        CursivisStoragePaths paths = CursivisStoragePaths.ForCurrentUser();
        var store = new WindowsCurrentUserSecretStore(
            new WindowsCurrentUserSecretStoreOptions(paths.SecretsDirectory, ApplicationPurpose));
        using SecretBuffer? secret = await store
            .ReadAsync(LogicalSecretName, cancellationToken)
            .ConfigureAwait(false);
        if (secret is null)
        {
            EnsureDataHubDefault();
            return false;
        }

        string temporary = secret.Use(static chars => new string(chars));
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", temporary, EnvironmentVariableTarget.Process);
            EnsureDataHubDefault();
            return true;
        }
        finally
        {
            // Strings cannot be zeroed in managed memory. Keep this copy scoped
            // to the process-environment handoff and never log or persist it.
            temporary = string.Empty;
        }
    }

    public static void EnsureDataHubDefault()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATAHUB_GRAPHQL_URL")))
        {
            Environment.SetEnvironmentVariable(
                "DATAHUB_GRAPHQL_URL",
                "http://localhost:8080/api/graphql",
                EnvironmentVariableTarget.Process);
        }
    }
}

public sealed class WindowsGeminiCredentialManager(ICurrentUserSecretStore store)
{
    private readonly ICurrentUserSecretStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public bool HasSavedKey => _store.Exists(WindowsGeminiCredentialStore.LogicalSecretName);

    public async Task SaveAsync(SecretBuffer replacement, CancellationToken cancellationToken = default)
    {
        await _store.SaveAsync(
            WindowsGeminiCredentialStore.LogicalSecretName,
            replacement,
            cancellationToken).ConfigureAwait(false);

        string temporary = replacement.Use(static chars => new string(chars));
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", temporary, EnvironmentVariableTarget.Process);
        }
        finally
        {
            temporary = string.Empty;
        }
    }

    public async Task<bool> DeleteAsync(CancellationToken cancellationToken = default)
    {
        bool deleted = await _store.DeleteAsync(
            WindowsGeminiCredentialStore.LogicalSecretName,
            cancellationToken).ConfigureAwait(false);
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", null, EnvironmentVariableTarget.Process);
        return deleted;
    }
}
