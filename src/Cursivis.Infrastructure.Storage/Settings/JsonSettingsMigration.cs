using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cursivis.Infrastructure.Storage.Persistence;

namespace Cursivis.Infrastructure.Storage.Settings;

public interface IJsonSettingsMigration
{
    int SourceVersion { get; }

    int TargetVersion { get; }

    JsonObject Migrate(JsonObject sourceData);
}

public sealed class DelegateJsonSettingsMigration : IJsonSettingsMigration
{
    private readonly Func<JsonObject, JsonObject> _migration;

    public DelegateJsonSettingsMigration(
        int sourceVersion,
        int targetVersion,
        Func<JsonObject, JsonObject> migration)
    {
        if (sourceVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceVersion));
        }

        if (targetVersion <= sourceVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(targetVersion));
        }

        SourceVersion = sourceVersion;
        TargetVersion = targetVersion;
        _migration = migration ?? throw new ArgumentNullException(nameof(migration));
    }

    public int SourceVersion { get; }

    public int TargetVersion { get; }

    public JsonObject Migrate(JsonObject sourceData) =>
        _migration((JsonObject)sourceData.DeepClone())
        ?? throw new InvalidOperationException("A settings migration returned no data.");
}

public sealed record SanitizedMigrationRecord(
    string DocumentName,
    int SourceVersion,
    int TargetVersion,
    string OutcomeCode,
    DateTimeOffset RecordedAtUtc);

public interface ISanitizedMigrationRecorder
{
    Task RecordAsync(
        SanitizedMigrationRecord record,
        CancellationToken cancellationToken = default);
}

public sealed class FileSanitizedMigrationRecorder : ISanitizedMigrationRecorder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _journalDirectory;
    private readonly AtomicFileWriter _writer = new(backupRetentionCount: 0);

    public FileSanitizedMigrationRecorder(string journalDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalDirectory);
        if (!Path.IsPathFullyQualified(journalDirectory))
        {
            throw new ArgumentException("The migration journal directory must be absolute.", nameof(journalDirectory));
        }

        _journalDirectory = Path.GetFullPath(journalDirectory);
    }

    public async Task RecordAsync(
        SanitizedMigrationRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var safeDocumentName = Path.GetFileName(record.DocumentName);
        if (!string.Equals(safeDocumentName, record.DocumentName, StringComparison.Ordinal) ||
            record.OutcomeCode.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("The migration record contains non-sanitized metadata.", nameof(record));
        }

        var safeRecord = record with { DocumentName = safeDocumentName };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(safeRecord, SerializerOptions);
        try
        {
            var fileName = string.Create(
                CultureInfo.InvariantCulture,
                $"migration-{record.RecordedAtUtc:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
            await _writer.WriteAsync(
                Path.Combine(_journalDirectory, fileName),
                bytes,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }
}
