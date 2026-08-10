using System.Collections.Concurrent;

namespace Cursivis.Infrastructure.Storage.Persistence;

public enum AtomicWriteStage
{
    TemporaryFileFlushed,
    BeforeCommit,
}

public interface IAtomicWriteFaultInjector
{
    void Inspect(AtomicWriteStage stage, string destinationFileName);
}

public sealed class AtomicFileWriter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DestinationLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly int _backupRetentionCount;
    private readonly IAtomicWriteFaultInjector? _faultInjector;

    public AtomicFileWriter(
        int backupRetentionCount = 3,
        IAtomicWriteFaultInjector? faultInjector = null)
    {
        if (backupRetentionCount is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(backupRetentionCount),
                "Backup retention must be between zero and ten.");
        }

        _backupRetentionCount = backupRetentionCount;
        _faultInjector = faultInjector;
    }

    public async Task WriteAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeDestination(destinationPath);
        var gate = DestinationLocks.GetOrAdd(normalizedPath, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteCoreAsync(normalizedPath, content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> CreateDurableCopyAsync(
        string sourcePath,
        string label,
        int retentionCount,
        CancellationToken cancellationToken = default)
    {
        var normalizedSource = NormalizeDestination(sourcePath);
        ValidateLabel(label);

        if (retentionCount is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionCount));
        }

        var directory = Path.GetDirectoryName(normalizedSource)!;
        var fileName = Path.GetFileName(normalizedSource);
        var copyPath = Path.Combine(directory, CreateSiblingName(fileName, label));

        await using (var source = new FileStream(
                         normalizedSource,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(
                         copyPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }

        TryPrune(directory, fileName, label, retentionCount);
        return copyPath;
    }

    public string MoveToQuarantine(string sourcePath, string label, int retentionCount = 3)
    {
        var normalizedSource = NormalizeDestination(sourcePath);
        ValidateLabel(label);

        if (retentionCount is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionCount));
        }

        var directory = Path.GetDirectoryName(normalizedSource)!;
        var fileName = Path.GetFileName(normalizedSource);
        var quarantinePath = Path.Combine(directory, CreateSiblingName(fileName, label));

        File.Move(normalizedSource, quarantinePath, overwrite: false);
        TryPrune(directory, fileName, label, retentionCount);
        return quarantinePath;
    }

    private async Task WriteCoreAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath)!;
        var fileName = Path.GetFileName(destinationPath);
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{fileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _faultInjector?.Inspect(AtomicWriteStage.TemporaryFileFlushed, fileName);
            cancellationToken.ThrowIfCancellationRequested();
            _faultInjector?.Inspect(AtomicWriteStage.BeforeCommit, fileName);

            if (File.Exists(destinationPath))
            {
                var backupPath = _backupRetentionCount == 0
                    ? null
                    : Path.Combine(directory, CreateSiblingName(fileName, "backup"));

                File.Replace(
                    temporaryPath,
                    destinationPath,
                    backupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
            }

            if (_backupRetentionCount > 0)
            {
                TryPrune(directory, fileName, "backup", _backupRetentionCount);
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static string NormalizeDestination(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Atomic writes require an absolute destination path.", nameof(path));
        }

        var normalized = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(Path.GetFileName(normalized)) || Path.GetDirectoryName(normalized) is null)
        {
            throw new ArgumentException("The destination must identify a file.", nameof(path));
        }

        return normalized;
    }

    private static string CreateSiblingName(string fileName, string label) =>
        $"{fileName}.{label}.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.{Guid.NewGuid():N}";

    private static void ValidateLabel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Length > 32 || label.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("The file label contains unsupported characters.", nameof(label));
        }
    }

    private static void TryPrune(
        string directory,
        string fileName,
        string label,
        int retentionCount)
    {
        try
        {
            var candidates = Directory
                .EnumerateFiles(directory, $"{fileName}.{label}.*", SearchOption.TopDirectoryOnly)
                .Select(static path => new FileInfo(path))
                .OrderByDescending(static file => file.CreationTimeUtc)
                .ThenByDescending(static file => file.Name, StringComparer.Ordinal)
                .Skip(retentionCount);

            foreach (var candidate in candidates)
            {
                TryDelete(candidate.FullName);
            }
        }
        catch (IOException)
        {
            // Retention cleanup is best effort after the durable commit.
        }
        catch (UnauthorizedAccessException)
        {
            // Retention cleanup is best effort after the durable commit.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A failed temp cleanup cannot make an already committed destination unsafe.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed temp cleanup cannot make an already committed destination unsafe.
        }
    }
}
