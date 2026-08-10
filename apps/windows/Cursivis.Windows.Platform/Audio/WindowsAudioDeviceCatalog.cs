using NAudio.Wave;

namespace Cursivis.Windows.Platform.Audio;

public sealed record WindowsMicrophoneEndpoint(string Id, string DisplayName);

public static class WindowsAudioDeviceCatalog
{
    public static IReadOnlyList<WindowsMicrophoneEndpoint> GetCaptureDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var endpoints = new List<WindowsMicrophoneEndpoint>();
        for (int index = 0; index < WaveIn.DeviceCount; index++)
        {
            WaveInCapabilities capabilities = WaveIn.GetCapabilities(index);
            endpoints.Add(new WindowsMicrophoneEndpoint(
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(capabilities.ProductName)
                    ? $"Microphone {index + 1}"
                    : capabilities.ProductName));
        }

        return endpoints;
    }

    public static int ParseDeviceNumber(string? endpointId) =>
        int.TryParse(
            endpointId,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out int value) && value >= 0 && value < WaveIn.DeviceCount
            ? value
            : 0;
}
