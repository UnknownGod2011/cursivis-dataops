namespace Cursivis.Infrastructure.Storage.Persistence;

public sealed class CursivisStoragePaths
{
    // The previous Cursivis product used %LocalAppData%\Cursivis for a
    // different runtime, hotkey host, native bridge, and configuration shape.
    // Cursivis Next must never read or overwrite that product's state.
    private const string DefaultApplicationDirectoryName = "Cursivis Next";
    private const string LegacyApplicationDirectoryName = "Cursivis";
    private const string ApiKeySecretFileName =
        "3703298939ce10d9107a6a0108d981591b481a449a6c9918027b1e85fe85e6b7.bin";

    private CursivisStoragePaths(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    public string QuickTaskFile => Path.Combine(RootDirectory, "quick-task.json");

    public string HotkeysFile => Path.Combine(RootDirectory, "hotkeys.json");

    public string MemoryFile => Path.Combine(RootDirectory, "memory.json");

    public string SecretsDirectory => Path.Combine(RootDirectory, "secrets");

    public string MigrationJournalDirectory => Path.Combine(RootDirectory, "migration-journal");

    public string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public string OverlayPlacementFile => Path.Combine(RootDirectory, "overlay-placement.json");

    public string LiveModePlacementFile => Path.Combine(RootDirectory, "live-mode-placement.json");

    public string ResultPanelSizeFile => Path.Combine(RootDirectory, "result-panel-size.json");

    public string ResultPanelPlacementFile => Path.Combine(RootDirectory, "result-panel-placement.json");

    public static CursivisStoragePaths ForCurrentUser()
    {
        string localApplicationData = GetCurrentUserLocalApplicationData();

        return new CursivisStoragePaths(
            Path.Combine(localApplicationData, DefaultApplicationDirectoryName));
    }

    /// <summary>
    /// Copies only Cursivis Next's known files out of the legacy product root
    /// on the first run after the storage-root correction. The legacy root is
    /// never modified, deleted, or used after migration.
    /// </summary>
    public static async Task<CursivisLegacyStorageMigrationResult> TryMigrateLegacyCurrentUserDataAsync(
        CursivisStoragePaths destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        // A destination directory means this installation already owns its
        // storage. Never resurrect deleted settings from the old product.
        if (Directory.Exists(destination.RootDirectory))
        {
            return CursivisLegacyStorageMigrationResult.NotRequired;
        }

        string legacyRoot = Path.Combine(
            GetCurrentUserLocalApplicationData(),
            LegacyApplicationDirectoryName);
        return await TryMigrateLegacyDataAsync(legacyRoot, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<CursivisLegacyStorageMigrationResult> TryMigrateLegacyDataAsync(
        string legacyRoot,
        CursivisStoragePaths destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Path.IsPathFullyQualified(legacyRoot))
        {
            throw new ArgumentException("The legacy storage root must be absolute.", nameof(legacyRoot));
        }

        if (Directory.Exists(destination.RootDirectory))
        {
            return CursivisLegacyStorageMigrationResult.NotRequired;
        }

        if (!Directory.Exists(legacyRoot))
        {
            return CursivisLegacyStorageMigrationResult.NotRequired;
        }

        var migrated = new List<string>();
        foreach ((string relativePath, long maximumBytes) in LegacyFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = Path.Combine(legacyRoot, relativePath);
            string target = Path.Combine(destination.RootDirectory, relativePath);
            if (await TryCopyIfMissingAsync(source, target, maximumBytes, cancellationToken)
                    .ConfigureAwait(false))
            {
                migrated.Add(relativePath);
            }
        }

        return new CursivisLegacyStorageMigrationResult(
            LegacyRootFound: true,
            MigratedRelativePaths: migrated);
    }

    private static readonly (string RelativePath, long MaximumBytes)[] LegacyFiles =
    [
        ("settings.json", 512 * 1024),
        ("quick-task.json", 128 * 1024),
        ("hotkeys.json", 64 * 1024),
        ("memory.json", 512 * 1024),
        ("overlay-placement.json", 32 * 1024),
        ("live-mode-placement.json", 32 * 1024),
        ("result-panel-size.json", 32 * 1024),
        ("result-panel-placement.json", 32 * 1024),
        (Path.Combine("secrets", ApiKeySecretFileName), 1024 * 1024),
    ];

    private static string GetCurrentUserLocalApplicationData()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("Current-user local application data is unavailable.");
        }

        return localApplicationData;
    }

    private static async Task<bool> TryCopyIfMissingAsync(
        string source,
        string target,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(target))
            {
                return false;
            }

            var sourceInfo = new FileInfo(source);
            if (sourceInfo.Length <= 0 || sourceInfo.Length > maximumBytes)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var sourceStream = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var targetStream = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await sourceStream.CopyToAsync(targetStream, 64 * 1024, cancellationToken)
                .ConfigureAwait(false);
            await targetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            targetStream.Flush(flushToDisk: true);
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

    public static CursivisStoragePaths ForRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The storage root must be an absolute path.", nameof(rootDirectory));
        }

        return new CursivisStoragePaths(rootDirectory);
    }
}

public sealed record CursivisLegacyStorageMigrationResult(
    bool LegacyRootFound,
    IReadOnlyList<string> MigratedRelativePaths)
{
    public static CursivisLegacyStorageMigrationResult NotRequired { get; } = new(false, []);

    public bool MigratedAny => MigratedRelativePaths.Count > 0;
}
