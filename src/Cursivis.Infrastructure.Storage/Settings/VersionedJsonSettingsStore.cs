using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cursivis.Infrastructure.Storage.Persistence;

namespace Cursivis.Infrastructure.Storage.Settings;

public sealed class VersionedJsonSettingsStore<T>
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        WriteIndented = true,
    };

    private readonly VersionedJsonSettingsStoreOptions _options;
    private readonly IJsonSettingsCodec<T> _codec;
    private readonly IReadOnlyDictionary<int, IJsonSettingsMigration> _migrations;
    private readonly AtomicFileWriter _writer;
    private readonly ISanitizedMigrationRecorder? _migrationRecorder;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VersionedJsonSettingsStore(
        VersionedJsonSettingsStoreOptions options,
        IJsonSettingsCodec<T> codec,
        IEnumerable<IJsonSettingsMigration>? migrations = null,
        ISanitizedMigrationRecorder? migrationRecorder = null,
        AtomicFileWriter? writer = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _migrationRecorder = migrationRecorder;
        _writer = writer ?? new AtomicFileWriter(options.BackupRetentionCount);

        var materializedMigrations = (migrations ?? [])
            .ToDictionary(static migration => migration.SourceVersion);

        foreach (var migration in materializedMigrations.Values)
        {
            if (migration.TargetVersion <= migration.SourceVersion ||
                migration.TargetVersion > options.CurrentSchemaVersion)
            {
                throw new ArgumentException("The settings migration chain is invalid.", nameof(migrations));
            }
        }

        _migrations = materializedMigrations;
    }

    public async Task<SettingsLoadResult<T>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_options.FilePath))
            {
                return CreateDefaultResult(SettingsLoadStatus.FirstRun);
            }

            JsonObject envelope;
            try
            {
                var bytes = await ReadBoundedAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    envelope = JsonNode.Parse(
                                   bytes,
                                   documentOptions: new JsonDocumentOptions
                                   {
                                       AllowTrailingCommas = false,
                                       CommentHandling = JsonCommentHandling.Disallow,
                                       MaxDepth = 64,
                                   }) as JsonObject
                               ?? throw new JsonException("The settings root must be an object.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            catch (IOException)
            {
                return CreateDefaultResult(SettingsLoadStatus.StorageUnavailable);
            }
            catch (UnauthorizedAccessException)
            {
                return CreateDefaultResult(SettingsLoadStatus.StorageUnavailable);
            }
            catch (JsonException)
            {
                return RecoverWholeFile(SettingsLoadStatus.RecoveredCorruptFile, "corrupt");
            }
            catch (InvalidDataException)
            {
                return RecoverWholeFile(SettingsLoadStatus.RecoveredCorruptFile, "corrupt");
            }

            if (!TryReadEnvelope(envelope, out var storedVersion, out var data))
            {
                return RecoverWholeFile(SettingsLoadStatus.RecoveredCorruptFile, "corrupt");
            }

            if (storedVersion > _options.CurrentSchemaVersion)
            {
                return RecoverWholeFile(
                    SettingsLoadStatus.RecoveredIncompatibleVersion,
                    "incompatible");
            }

            if (storedVersion < _options.CurrentSchemaVersion)
            {
                return await MigrateAsync(storedVersion, data!, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var decoded = _codec.Decode(data!, SerializerOptions);
                if (!decoded.Recovered)
                {
                    return new SettingsLoadResult<T>(
                        decoded.Value,
                        SettingsLoadStatus.Loaded,
                        _options.CurrentSchemaVersion,
                        [],
                        null,
                        RepairPersisted: true,
                        MigrationResultRecorded: false);
                }

                string? recoveryArtifact = null;
                var repairPersisted = false;
                try
                {
                    recoveryArtifact = Path.GetFileName(
                        await _writer.CreateDurableCopyAsync(
                            _options.FilePath,
                            "pre-recovery",
                            _options.BackupRetentionCount,
                            cancellationToken).ConfigureAwait(false));
                    await SaveCoreAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
                    repairPersisted = true;
                }
                catch (IOException)
                {
                    repairPersisted = false;
                }
                catch (UnauthorizedAccessException)
                {
                    repairPersisted = false;
                }

                return new SettingsLoadResult<T>(
                    decoded.Value,
                    SettingsLoadStatus.RecoveredInvalidValues,
                    _options.CurrentSchemaVersion,
                    decoded.ResetTopLevelProperties,
                    recoveryArtifact,
                    repairPersisted,
                    MigrationResultRecorded: false);
            }
            catch (JsonException)
            {
                return RecoverWholeFile(SettingsLoadStatus.RecoveredCorruptFile, "corrupt");
            }
            catch (NotSupportedException)
            {
                return RecoverWholeFile(SettingsLoadStatus.RecoveredCorruptFile, "corrupt");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SettingsLoadResult<T>> MigrateAsync(
        int sourceVersion,
        JsonObject sourceData,
        CancellationToken cancellationToken)
    {
        string? backupFileName = null;
        var migrationResultRecorded = false;

        try
        {
            backupFileName = Path.GetFileName(
                await _writer.CreateDurableCopyAsync(
                    _options.FilePath,
                    "pre-migration",
                    _options.BackupRetentionCount,
                    cancellationToken).ConfigureAwait(false));

            var workingVersion = sourceVersion;
            var workingData = (JsonObject)sourceData.DeepClone();

            while (workingVersion < _options.CurrentSchemaVersion)
            {
                if (!_migrations.TryGetValue(workingVersion, out var migration) ||
                    migration.TargetVersion <= workingVersion)
                {
                    throw new InvalidOperationException("No explicit migration exists for the stored schema.");
                }

                workingData = migration.Migrate(workingData);
                workingVersion = migration.TargetVersion;
            }

            if (workingVersion != _options.CurrentSchemaVersion)
            {
                throw new InvalidOperationException("The migration chain did not reach the current schema.");
            }

            var decoded = _codec.Decode(workingData, SerializerOptions);
            await SaveCoreAsync(decoded.Value, cancellationToken).ConfigureAwait(false);
            migrationResultRecorded = await TryRecordMigrationAsync(
                sourceVersion,
                _options.CurrentSchemaVersion,
                "migration-complete",
                cancellationToken).ConfigureAwait(false);

            return new SettingsLoadResult<T>(
                decoded.Value,
                SettingsLoadStatus.Migrated,
                _options.CurrentSchemaVersion,
                decoded.ResetTopLevelProperties,
                backupFileName,
                RepairPersisted: true,
                migrationResultRecorded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            JsonException or NotSupportedException or InvalidOperationException)
        {
            migrationResultRecorded = await TryRecordMigrationAsync(
                sourceVersion,
                _options.CurrentSchemaVersion,
                "migration-failed",
                CancellationToken.None).ConfigureAwait(false);

            return new SettingsLoadResult<T>(
                _codec.CreateDefault(),
                SettingsLoadStatus.MigrationFailed,
                _options.CurrentSchemaVersion,
                [],
                backupFileName,
                RepairPersisted: false,
                migrationResultRecorded);
        }
    }

    private async Task SaveCoreAsync(T value, CancellationToken cancellationToken)
    {
        var data = _codec.Encode(value, SerializerOptions);
        var envelope = new JsonObject
        {
            ["schemaVersion"] = _options.CurrentSchemaVersion,
            ["savedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["data"] = data,
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        try
        {
            if (bytes.Length > _options.MaximumFileBytes)
            {
                throw new InvalidOperationException("The settings document exceeds the configured size limit.");
            }

            await _writer.WriteAsync(
                _options.FilePath,
                bytes,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<byte[]> ReadBoundedAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _options.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length is <= 0 || stream.Length > _options.MaximumFileBytes)
        {
            throw new InvalidDataException("The settings file size is invalid.");
        }

        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private SettingsLoadResult<T> RecoverWholeFile(SettingsLoadStatus status, string label)
    {
        string? artifact = null;
        try
        {
            artifact = Path.GetFileName(
                _writer.MoveToQuarantine(
                    _options.FilePath,
                    label,
                    _options.BackupRetentionCount));
        }
        catch (IOException)
        {
            artifact = null;
        }
        catch (UnauthorizedAccessException)
        {
            artifact = null;
        }

        return new SettingsLoadResult<T>(
            _codec.CreateDefault(),
            status,
            _options.CurrentSchemaVersion,
            [],
            artifact,
            RepairPersisted: false,
            MigrationResultRecorded: false);
    }

    private SettingsLoadResult<T> CreateDefaultResult(SettingsLoadStatus status) =>
        new(
            _codec.CreateDefault(),
            status,
            _options.CurrentSchemaVersion,
            [],
            null,
            RepairPersisted: status == SettingsLoadStatus.FirstRun,
            MigrationResultRecorded: false);

    private async Task<bool> TryRecordMigrationAsync(
        int sourceVersion,
        int targetVersion,
        string outcomeCode,
        CancellationToken cancellationToken)
    {
        if (_migrationRecorder is null)
        {
            return false;
        }

        try
        {
            await _migrationRecorder.RecordAsync(
                new SanitizedMigrationRecord(
                    Path.GetFileName(_options.FilePath),
                    sourceVersion,
                    targetVersion,
                    outcomeCode,
                    DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryReadEnvelope(
        JsonObject envelope,
        out int schemaVersion,
        out JsonObject? data)
    {
        schemaVersion = 0;
        data = null;

        if (envelope["schemaVersion"] is not JsonValue versionNode ||
            !versionNode.TryGetValue<int>(out schemaVersion) ||
            schemaVersion < 1 ||
            envelope["data"] is not JsonObject dataObject)
        {
            return false;
        }

        data = dataObject;
        return true;
    }
}
