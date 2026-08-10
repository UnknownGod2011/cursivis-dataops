using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cursivis.Infrastructure.Storage.Settings;

public enum SettingsLoadStatus
{
    FirstRun,
    Loaded,
    Migrated,
    RecoveredInvalidValues,
    RecoveredCorruptFile,
    RecoveredIncompatibleVersion,
    MigrationFailed,
    StorageUnavailable,
}

public sealed record SettingsLoadResult<T>(
    T Value,
    SettingsLoadStatus Status,
    int EffectiveSchemaVersion,
    IReadOnlyList<string> ResetTopLevelProperties,
    string? RecoveryArtifactFileName,
    bool RepairPersisted,
    bool MigrationResultRecorded);

public sealed record SettingsDecodeResult<T>(
    T Value,
    bool Recovered,
    IReadOnlyList<string> ResetTopLevelProperties);

public interface IJsonSettingsCodec<T>
{
    T CreateDefault();

    JsonObject Encode(T value, JsonSerializerOptions serializerOptions);

    SettingsDecodeResult<T> Decode(
        JsonObject data,
        JsonSerializerOptions serializerOptions);
}

public sealed class DefaultMergingJsonSettingsCodec<T> : IJsonSettingsCodec<T>
{
    private readonly Func<T> _defaultFactory;
    private readonly Func<T, bool> _validator;

    public DefaultMergingJsonSettingsCodec(
        Func<T> defaultFactory,
        Func<T, bool>? validator = null)
    {
        _defaultFactory = defaultFactory ?? throw new ArgumentNullException(nameof(defaultFactory));
        _validator = validator ?? (_ => true);
    }

    public T CreateDefault()
    {
        var value = _defaultFactory();
        if (value is null || !_validator(value))
        {
            throw new InvalidOperationException("The settings default factory produced an invalid value.");
        }

        return value;
    }

    public JsonObject Encode(T value, JsonSerializerOptions serializerOptions)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(serializerOptions);

        if (!_validator(value))
        {
            throw new InvalidOperationException("Settings validation failed before persistence.");
        }

        return JsonSerializer.SerializeToNode(value, serializerOptions) as JsonObject
            ?? throw new InvalidOperationException("Settings must serialize to a JSON object.");
    }

    public SettingsDecodeResult<T> Decode(
        JsonObject data,
        JsonSerializerOptions serializerOptions)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(serializerOptions);

        var defaults = Encode(CreateDefault(), serializerOptions);
        var merged = (JsonObject)defaults.DeepClone();
        var resetProperties = new List<string>();
        var ignoredUnknownProperty = false;

        foreach (var property in data)
        {
            if (!defaults.ContainsKey(property.Key))
            {
                ignoredUnknownProperty = true;
                continue;
            }

            var candidate = (JsonObject)merged.DeepClone();
            candidate[property.Key] = property.Value?.DeepClone();

            try
            {
                var decodedCandidate = candidate.Deserialize<T>(serializerOptions);
                if (decodedCandidate is null || !_validator(decodedCandidate))
                {
                    resetProperties.Add(property.Key);
                    continue;
                }

                merged = candidate;
            }
            catch (JsonException)
            {
                resetProperties.Add(property.Key);
            }
            catch (NotSupportedException)
            {
                resetProperties.Add(property.Key);
            }
        }

        var decoded = merged.Deserialize<T>(serializerOptions);
        if (decoded is null || !_validator(decoded))
        {
            throw new JsonException("The recovered settings document is invalid.");
        }

        return new SettingsDecodeResult<T>(
            decoded,
            resetProperties.Count > 0 || ignoredUnknownProperty,
            resetProperties.AsReadOnly());
    }
}

public sealed record VersionedJsonSettingsStoreOptions
{
    public VersionedJsonSettingsStoreOptions(
        string filePath,
        int currentSchemaVersion,
        int backupRetentionCount = 3,
        int maximumFileBytes = 4 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new ArgumentException("The settings file path must be absolute.", nameof(filePath));
        }

        if (currentSchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(currentSchemaVersion));
        }

        if (backupRetentionCount is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(backupRetentionCount));
        }

        if (maximumFileBytes is < 1024 or > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        FilePath = Path.GetFullPath(filePath);
        CurrentSchemaVersion = currentSchemaVersion;
        BackupRetentionCount = backupRetentionCount;
        MaximumFileBytes = maximumFileBytes;
    }

    public string FilePath { get; }

    public int CurrentSchemaVersion { get; }

    public int BackupRetentionCount { get; }

    public int MaximumFileBytes { get; }
}
