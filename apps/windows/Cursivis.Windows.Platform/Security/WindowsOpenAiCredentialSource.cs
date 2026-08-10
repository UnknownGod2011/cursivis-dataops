using Cursivis.Application.OpenAI;
using Cursivis.Infrastructure.Storage.Development;
using Cursivis.Infrastructure.Storage.Security;

namespace Cursivis.Windows.Platform.Security;

public sealed class WindowsOpenAiCredentialSource(
    ICurrentUserSecretStore productionStore,
    DevelopmentOpenAiSecretProvider? developmentProvider = null,
    bool allowDevelopmentFallback = false) : IOpenAiCredentialSource
{
    public const string LogicalSecretName = "openai-api-key";

    private readonly ICurrentUserSecretStore _productionStore = productionStore
        ?? throw new ArgumentNullException(nameof(productionStore));
    private readonly DevelopmentOpenAiSecretProvider? _developmentProvider = developmentProvider;
    private readonly bool _allowDevelopmentFallback = allowDevelopmentFallback;

    public async Task<T> UseApiKeyAsync<T>(
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using SecretBuffer? productionSecret = await _productionStore
            .ReadAsync(LogicalSecretName, cancellationToken)
            .ConfigureAwait(false);
        if (productionSecret is not null)
        {
            return await InvokeAsync(productionSecret, operation, cancellationToken).ConfigureAwait(false);
        }

        if (!_allowDevelopmentFallback || _developmentProvider is null)
        {
            throw new OpenAiCredentialUnavailableException();
        }

        using DevelopmentSecretLoadResult development = await _developmentProvider
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (development.Status != DevelopmentSecretLoadStatus.Success || development.Secret is null)
        {
            throw new OpenAiCredentialUnavailableException();
        }

        return await InvokeAsync(development.Secret, operation, cancellationToken).ConfigureAwait(false);
    }

    private static Task<T> InvokeAsync<T>(
        SecretBuffer secret,
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        string temporary = secret.Use(static value => new string(value));
        return operation(temporary, cancellationToken);
    }
}

public sealed class WindowsOpenAiCredentialManager(ICurrentUserSecretStore store)
{
    private readonly ICurrentUserSecretStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public bool HasSavedKey => _store.Exists(WindowsOpenAiCredentialSource.LogicalSecretName);

    public Task SaveAsync(SecretBuffer replacement, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(WindowsOpenAiCredentialSource.LogicalSecretName, replacement, cancellationToken);

    public Task<bool> DeleteAsync(CancellationToken cancellationToken = default) =>
        _store.DeleteAsync(WindowsOpenAiCredentialSource.LogicalSecretName, cancellationToken);
}
