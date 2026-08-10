using System.Text.Json;
using Cursivis.Infrastructure.Storage.Persistence;
using Cursivis.Windows.Platform.Overlays;

namespace Cursivis.Windows.App.Helpers;

internal sealed class OverlayPlacementStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;
    private readonly AtomicFileWriter _writer = new(backupRetentionCount: 1);

    public OverlayPlacementStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public OverlayPoint? TryLoad()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            SavedPlacement? value = JsonSerializer.Deserialize<SavedPlacement>(
                File.ReadAllBytes(_filePath),
                SerializerOptions);
            return value is null ? null : new OverlayPoint(value.X, value.Y);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(OverlayPoint point, CancellationToken cancellationToken = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new SavedPlacement(point.X, point.Y),
            SerializerOptions);
        await _writer.WriteAsync(_filePath, bytes, cancellationToken).ConfigureAwait(false);
    }

    public void Reset()
    {
        try
        {
            File.Delete(_filePath);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed record SavedPlacement(int X, int Y);
}
