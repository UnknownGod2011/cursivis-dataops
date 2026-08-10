using Cursivis.Infrastructure.Storage.Security;

namespace Cursivis.Infrastructure.Storage.Development;

public enum DevelopmentSecretLoadStatus
{
    Success,
    Disabled,
    Missing,
    Malformed,
    Unavailable,
}

public enum DevelopmentSecretSource
{
    None,
    ProcessEnvironment,
    DotEnvFile,
}

public sealed record DevelopmentOpenAiSecretOptions
{
    public DevelopmentOpenAiSecretOptions(bool enabled, string? projectRoot)
    {
        if (enabled && (string.IsNullOrWhiteSpace(projectRoot) || !Path.IsPathFullyQualified(projectRoot)))
        {
            throw new ArgumentException(
                "An absolute project root is required when development secret loading is enabled.",
                nameof(projectRoot));
        }

        Enabled = enabled;
        ProjectRoot = projectRoot is null ? string.Empty : Path.GetFullPath(projectRoot);
    }

    public bool Enabled { get; }

    public string ProjectRoot { get; }
}

public interface IDevelopmentEnvironmentVariableSource
{
    string? GetVariable(string name);
}

public sealed class ProcessDevelopmentEnvironmentVariableSource : IDevelopmentEnvironmentVariableSource
{
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);
}

public sealed class DevelopmentSecretLoadResult : IDisposable
{
    private DevelopmentSecretLoadResult(
        DevelopmentSecretLoadStatus status,
        DevelopmentSecretSource source,
        SecretBuffer? secret)
    {
        Status = status;
        Source = source;
        Secret = secret;
    }

    public DevelopmentSecretLoadStatus Status { get; }

    public DevelopmentSecretSource Source { get; }

    public SecretBuffer? Secret { get; }

    public static DevelopmentSecretLoadResult Succeeded(
        DevelopmentSecretSource source,
        SecretBuffer secret) => new(DevelopmentSecretLoadStatus.Success, source, secret);

    public static DevelopmentSecretLoadResult Failed(DevelopmentSecretLoadStatus status) =>
        new(status, DevelopmentSecretSource.None, null);

    public override string ToString() => $"{Status} ({Source})";

    public void Dispose() => Secret?.Dispose();
}

public sealed class DevelopmentOpenAiSecretProvider
{
    public const string VariableName = "OPENAI_API_KEY";
    private const int MaximumSecretCharacters = 16 * 1024;
    private const int MaximumEnvironmentFileBytes = 1024 * 1024;

    private readonly DevelopmentOpenAiSecretOptions _options;
    private readonly IDevelopmentEnvironmentVariableSource _environment;

    public DevelopmentOpenAiSecretProvider(
        DevelopmentOpenAiSecretOptions options,
        IDevelopmentEnvironmentVariableSource? environment = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? new ProcessDevelopmentEnvironmentVariableSource();
    }

    public async Task<DevelopmentSecretLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return DevelopmentSecretLoadResult.Failed(DevelopmentSecretLoadStatus.Disabled);
        }

        var processValue = _environment.GetVariable(VariableName);
        if (processValue is not null)
        {
            return TryCreateSecretFromString(processValue, out var processSecret)
                ? DevelopmentSecretLoadResult.Succeeded(
                    DevelopmentSecretSource.ProcessEnvironment,
                    processSecret!)
                : DevelopmentSecretLoadResult.Failed(DevelopmentSecretLoadStatus.Malformed);
        }

        var environmentFile = Path.Combine(_options.ProjectRoot, ".env");
        if (!File.Exists(environmentFile))
        {
            return DevelopmentSecretLoadResult.Failed(DevelopmentSecretLoadStatus.Missing);
        }

        try
        {
            using var stream = new FileStream(
                environmentFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            if (stream.Length is <= 0 or > MaximumEnvironmentFileBytes)
            {
                return DevelopmentSecretLoadResult.Failed(DevelopmentSecretLoadStatus.Malformed);
            }

            SecretBuffer? discovered = null;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!TryReadSecretAssignment(line, out var isTargetVariable, out var lineSecret))
                {
                    return DevelopmentSecretLoadResult.Failed(DevelopmentSecretLoadStatus.Malformed);
                }

                if (!isTargetVariable)
                {
                    continue;
                }

                if (discovered is not null || lineSecret is null)
                {
                    discovered?.Dispose();
                    lineSecret?.Dispose();
                    return DevelopmentSecretLoadResult.Failed(DevelopmentSecretLoadStatus.Malformed);
                }

                discovered = lineSecret;
            }

            return discovered is null
                ? DevelopmentSecretLoadResult.Failed(DevelopmentSecretLoadStatus.Missing)
                : DevelopmentSecretLoadResult.Succeeded(
                    DevelopmentSecretSource.DotEnvFile,
                    discovered);
        }
        catch (IOException)
        {
            return DevelopmentSecretLoadResult.Failed(DevelopmentSecretLoadStatus.Unavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return DevelopmentSecretLoadResult.Failed(DevelopmentSecretLoadStatus.Unavailable);
        }
    }

    private static bool TryCreateSecret(ReadOnlySpan<char> value, out SecretBuffer? secret)
    {
        if (value.IsEmpty || value.Length > MaximumSecretCharacters)
        {
            secret = null;
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                secret = null;
                return false;
            }
        }

        secret = new SecretBuffer(value);
        return true;
    }

    private static bool TryCreateSecretFromString(string value, out SecretBuffer? secret) =>
        TryCreateSecret(value.AsSpan(), out secret);

    private static bool TryReadSecretAssignment(
        string line,
        out bool isTargetVariable,
        out SecretBuffer? secret)
    {
        var span = line.AsSpan().Trim();
        isTargetVariable = false;
        secret = null;

        if (span.IsEmpty || span[0] == '#')
        {
            return true;
        }

        const string exportPrefix = "export ";
        if (span.StartsWith(exportPrefix, StringComparison.Ordinal))
        {
            span = span[exportPrefix.Length..].TrimStart();
        }

        var separator = span.IndexOf('=');
        if (separator < 0 || !span[..separator].Trim().Equals(VariableName, StringComparison.Ordinal))
        {
            return true;
        }

        isTargetVariable = true;
        var value = span[(separator + 1)..].Trim();
        if (!value.IsEmpty &&
            (value[0] is '\"' or '\'' || value[^1] is '\"' or '\''))
        {
            if (value.Length < 2 || value[0] != value[^1] || value[0] is not ('\"' or '\''))
            {
                return false;
            }

            value = value[1..^1];
        }

        return TryCreateSecret(value, out secret);
    }
}
