using System.Text.Json;
using Cursivis.Infrastructure.Storage.Persistence;
using Cursivis.Windows.Platform.Overlays;

namespace Cursivis.Windows.App.Helpers;

internal sealed class OverlaySizeStore
{
    private const int CurrentVersion = 2;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;
    private readonly AtomicFileWriter _writer = new(backupRetentionCount: 1);

    public OverlaySizeStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public OverlaySize? TryLoad()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            SavedSize? value = JsonSerializer.Deserialize<SavedSize>(
                File.ReadAllBytes(_filePath),
                SerializerOptions);
            return value is
                {
                    Version: CurrentVersion,
                    Width: >= 516 and <= 1600,
                    Height: >= 232 and <= 1200,
                }
                ? new OverlaySize(value.Width, value.Height)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public Task SaveAsync(OverlaySize size, CancellationToken cancellationToken = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new SavedSize(CurrentVersion, size.Width, size.Height),
            SerializerOptions);
        return _writer.WriteAsync(_filePath, bytes, cancellationToken);
    }

    private sealed record SavedSize(int Version, int Width, int Height);
}
