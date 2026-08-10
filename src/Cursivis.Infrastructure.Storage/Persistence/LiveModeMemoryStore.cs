using Cursivis.Application.Realtime;
using Cursivis.Infrastructure.Storage.Settings;

namespace Cursivis.Infrastructure.Storage.Persistence;

/// <summary>
/// Persists only short facts that the user explicitly asks Live Mode to remember.
/// Conversation transcripts never cross this boundary.
/// </summary>
public sealed class LiveModeMemoryStore : ILiveModeMemoryStore
{
    private const int MaximumEntries = 50;
    private const int MaximumEntryCharacters = 500;
    private readonly VersionedJsonSettingsStore<LiveModeMemoryDocument> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LiveModeMemoryDocument? _cached;

    public LiveModeMemoryStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _store = new VersionedJsonSettingsStore<LiveModeMemoryDocument>(
            new VersionedJsonSettingsStoreOptions(
                filePath,
                LiveModeMemoryDocument.CurrentSchemaVersion,
                maximumFileBytes: 256 * 1024),
            new DefaultMergingJsonSettingsCodec<LiveModeMemoryDocument>(
                static () => LiveModeMemoryDocument.Empty,
                IsValid));
    }

    public async Task<LiveModeMemorySnapshot> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ToSnapshot(await LoadCoreAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LiveModeMemorySnapshot> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LiveModeMemoryDocument current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (current.IsEnabled != enabled)
            {
                current = current with { IsEnabled = enabled };
                await SaveCoreAsync(current, cancellationToken).ConfigureAwait(false);
            }

            return ToSnapshot(current);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LiveModeMemorySaveResult> RememberExplicitAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string normalized = string.Join(' ', text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length is < 2 or > MaximumEntryCharacters)
        {
            return new(
                LiveModeMemorySaveStatus.Rejected,
                null,
                $"A saved memory must contain between 2 and {MaximumEntryCharacters} characters.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LiveModeMemoryDocument current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!current.IsEnabled)
            {
                return new(
                    LiveModeMemorySaveStatus.Disabled,
                    null,
                    "Live Mode memory is off. Enable it in Privacy & Safety first.");
            }

            LiveModeMemoryEntry? duplicate = current.Entries.FirstOrDefault(
                entry => string.Equals(entry.Text, normalized, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
            {
                return new(LiveModeMemorySaveStatus.Saved, duplicate, "That memory was already saved.");
            }

            var entry = new LiveModeMemoryEntry(Guid.NewGuid(), normalized, DateTimeOffset.UtcNow);
            IReadOnlyList<LiveModeMemoryEntry> entries = current.Entries
                .Append(entry)
                .OrderByDescending(static item => item.CreatedAtUtc)
                .Take(MaximumEntries)
                .ToArray();
            current = current with { Entries = entries };
            await SaveCoreAsync(current, cancellationToken).ConfigureAwait(false);
            return new(LiveModeMemorySaveStatus.Saved, entry, "Saved to Live Mode memory.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LiveModeMemoryDocument current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            LiveModeMemoryEntry[] entries = current.Entries.Where(entry => entry.Id != id).ToArray();
            if (entries.Length == current.Entries.Count)
            {
                return false;
            }

            await SaveCoreAsync(current with { Entries = entries }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LiveModeMemoryDocument current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (current.Entries.Count > 0)
            {
                await SaveCoreAsync(current with { Entries = [] }, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LiveModeMemoryDocument> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        SettingsLoadResult<LiveModeMemoryDocument> loaded = await _store.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        _cached = loaded.Value;
        if (loaded.Status == SettingsLoadStatus.FirstRun)
        {
            await _store.SaveAsync(_cached, cancellationToken).ConfigureAwait(false);
        }

        return _cached;
    }

    private async Task SaveCoreAsync(
        LiveModeMemoryDocument document,
        CancellationToken cancellationToken)
    {
        await _store.SaveAsync(document, cancellationToken).ConfigureAwait(false);
        _cached = document;
    }

    private static LiveModeMemorySnapshot ToSnapshot(LiveModeMemoryDocument document) =>
        new(document.IsEnabled, document.Entries.ToArray());

    private static bool IsValid(LiveModeMemoryDocument document) =>
        document.SchemaVersion == LiveModeMemoryDocument.CurrentSchemaVersion &&
        document.Entries is not null &&
        document.Entries.Count <= MaximumEntries &&
        document.Entries.All(entry =>
            entry.Id != Guid.Empty &&
            !string.IsNullOrWhiteSpace(entry.Text) &&
            entry.Text.Length <= MaximumEntryCharacters &&
            entry.CreatedAtUtc.Offset == TimeSpan.Zero);

    public sealed record LiveModeMemoryDocument(
        int SchemaVersion,
        bool IsEnabled,
        IReadOnlyList<LiveModeMemoryEntry> Entries)
    {
        public const int CurrentSchemaVersion = 1;

        public static LiveModeMemoryDocument Empty { get; } = new(
            CurrentSchemaVersion,
            IsEnabled: false,
            Entries: []);
    }
}
