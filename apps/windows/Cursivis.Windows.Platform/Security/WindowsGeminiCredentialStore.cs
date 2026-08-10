using System.Text.Json;
using Cursivis.Infrastructure.Storage.Persistence;
using Cursivis.Infrastructure.Storage.Security;

namespace Cursivis.Windows.Platform.Security;

/// <summary>
/// Keeps the hackathon Gemini credential separate from the inherited optional
/// OpenAI voice/realtime credential. Secrets are protected with the same
/// current-user Windows store Cursivis already uses. Non-secret model/endpoint
/// preferences live in a small current-user JSON file.
/// </summary>
public static class WindowsGeminiCredentialStore
{
    public const string LogicalSecretName = "gemini-api-key";
    private const string ApplicationPurpose = "Cursivis.Gemini.ApiKey.v1";
    private const string ConnectionSettingsFileName = "dataops-connection.json";

    public static WindowsGeminiCredentialManager CreateManager()
    {
        CursivisStoragePaths paths = CursivisStoragePaths.ForCurrentUser();
        var store = new WindowsCurrentUserSecretStore(
            new WindowsCurrentUserSecretStoreOptions(paths.SecretsDirectory, ApplicationPurpose));
        return new WindowsGeminiCredentialManager(store);
    }

    public static DataOpsConnectionSettings LoadConnectionSettings()
    {
        CursivisStoragePaths paths = CursivisStoragePaths.ForCurrentUser();
        string file = Path.Combine(paths.RootDirectory, ConnectionSettingsFileName);
        if (!File.Exists(file))
        {
            return DataOpsConnectionSettings.Default;
        }

        try
        {
            var info = new FileInfo(file);
            if (info.Length <= 0 || info.Length > 64 * 1024)
            {
                return DataOpsConnectionSettings.Default;
            }

            string json = File.ReadAllText(file);
            DataOpsConnectionSettings? parsed = JsonSerializer.Deserialize<DataOpsConnectionSettings>(json);
            return parsed?.Normalize() ?? DataOpsConnectionSettings.Default;
        }
        catch (IOException)
        {
            return DataOpsConnectionSettings.Default;
        }
        catch (UnauthorizedAccessException)
        {
            return DataOpsConnectionSettings.Default;
        }
        catch (JsonException)
        {
            return DataOpsConnectionSettings.Default;
        }
    }

    public static async Task SaveConnectionSettingsAsync(
        DataOpsConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DataOpsConnectionSettings normalized = settings.Normalize();
        CursivisStoragePaths paths = CursivisStoragePaths.ForCurrentUser();
        Directory.CreateDirectory(paths.RootDirectory);
        string target = Path.Combine(paths.RootDirectory, ConnectionSettingsFileName);
        string temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        string json = JsonSerializer.Serialize(
            normalized,
            new JsonSerializerOptions { WriteIndented = true });

        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        ApplyConnectionSettings(normalized);
    }

    public static async Task<bool> LoadIntoProcessEnvironmentAsync(
        CancellationToken cancellationToken = default)
    {
        ApplyConnectionSettings(LoadConnectionSettings());
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY")))
        {
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
            return false;
        }

        string temporary = secret.Use(static chars => new string(chars));
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", temporary, EnvironmentVariableTarget.Process);
            return true;
        }
        finally
        {
            // Managed strings cannot be zeroed. Scope the transient plaintext to
            // this process-environment handoff and never log or persist it.
            temporary = string.Empty;
        }
    }

    private static void ApplyConnectionSettings(DataOpsConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_MODEL")))
        {
            Environment.SetEnvironmentVariable(
                "GEMINI_MODEL",
                settings.GeminiModel,
                EnvironmentVariableTarget.Process);
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATAHUB_GRAPHQL_URL")))
        {
            Environment.SetEnvironmentVariable(
                "DATAHUB_GRAPHQL_URL",
                settings.DataHubGraphQlUrl,
                EnvironmentVariableTarget.Process);
        }
    }
}

public sealed record DataOpsConnectionSettings(string GeminiModel, string DataHubGraphQlUrl)
{
    public const string DefaultGeminiModel = "gemini-2.5-flash";
    public const string DefaultDataHubGraphQlUrl = "http://localhost:8080/api/graphql";

    public static DataOpsConnectionSettings Default { get; } =
        new(DefaultGeminiModel, DefaultDataHubGraphQlUrl);

    public DataOpsConnectionSettings Normalize()
    {
        string model = string.IsNullOrWhiteSpace(GeminiModel)
            ? DefaultGeminiModel
            : GeminiModel.Trim();
        string endpoint = string.IsNullOrWhiteSpace(DataHubGraphQlUrl)
            ? DefaultDataHubGraphQlUrl
            : DataHubGraphQlUrl.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            endpoint = DefaultDataHubGraphQlUrl;
        }

        return new DataOpsConnectionSettings(model, endpoint);
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
