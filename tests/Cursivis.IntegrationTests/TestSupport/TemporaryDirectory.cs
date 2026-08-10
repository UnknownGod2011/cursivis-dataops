namespace Cursivis.IntegrationTests.TestSupport;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "cursivis-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // The OS can briefly retain handles after a failed test; the unique temp root is safe to leave.
        }
        catch (UnauthorizedAccessException)
        {
            // The OS can briefly retain handles after a failed test; the unique temp root is safe to leave.
        }
    }
}
