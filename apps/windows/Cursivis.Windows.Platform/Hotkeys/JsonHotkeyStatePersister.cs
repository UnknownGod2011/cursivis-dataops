using System.Text.Json;
using Cursivis.Infrastructure.Storage.Persistence;

namespace Cursivis.Windows.Platform.Hotkeys;

public sealed class JsonHotkeyStatePersister : IHotkeyStatePersister
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly string _filePath;
    private readonly AtomicFileWriter _writer = new(3);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonHotkeyStatePersister(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new ArgumentException("The hotkey settings path must be absolute.", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
    }

    public async Task PersistAsync(
        string commandName,
        HotkeyChord chord,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HotkeyFile document = TryRead() ?? new HotkeyFile(CurrentVersion, new Dictionary<string, HotkeyValue>(StringComparer.Ordinal));
            var bindings = new Dictionary<string, HotkeyValue>(document.Bindings, StringComparer.Ordinal)
            {
                [commandName] = new HotkeyValue((uint)chord.Modifiers, chord.VirtualKey),
            };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                new HotkeyFile(CurrentVersion, bindings),
                SerializerOptions);
            if (bytes.Length > 64 * 1024)
            {
                throw new InvalidOperationException("The hotkey settings file exceeds its safety limit.");
            }

            await _writer.WriteAsync(_filePath, bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryLoad(string commandName, out HotkeyChord chord)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        HotkeyFile? document = TryRead();
        if (document?.Bindings.TryGetValue(commandName, out HotkeyValue? value) == true &&
            HotkeyChord.Validate((HotkeyModifiers)value.Modifiers, value.VirtualKey) == HotkeyChordValidationCode.Valid)
        {
            chord = new HotkeyChord((HotkeyModifiers)value.Modifiers, value.VirtualKey);
            return true;
        }

        chord = default;
        return false;
    }

    private HotkeyFile? TryRead()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var info = new FileInfo(_filePath);
            if (info.Length is <= 0 or > 64 * 1024)
            {
                return null;
            }

            HotkeyFile? document = JsonSerializer.Deserialize<HotkeyFile>(
                File.ReadAllBytes(_filePath),
                SerializerOptions);
            return document is { Version: CurrentVersion, Bindings: not null }
                ? document
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record HotkeyFile(int Version, IReadOnlyDictionary<string, HotkeyValue> Bindings);

    private sealed record HotkeyValue(uint Modifiers, uint VirtualKey);
}
