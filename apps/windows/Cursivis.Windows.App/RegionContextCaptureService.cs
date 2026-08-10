using System.Security.Cryptography;
using Cursivis.Application.Context;
using Cursivis.Domain.Context;
using Cursivis.Windows.Platform.Capture;
using Cursivis.Windows.Platform.Foreground;

namespace Cursivis.Windows.App;

internal sealed class RegionContextCaptureService : IRegionContextCaptureService
{
    private readonly IForegroundWindowIdentityProvider _foreground;
    private readonly WindowsScreenCaptureService _screenCapture;

    public RegionContextCaptureService(
        IForegroundWindowIdentityProvider foreground,
        WindowsScreenCaptureService screenCapture)
    {
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _screenCapture = screenCapture ?? throw new ArgumentNullException(nameof(screenCapture));
    }

    public async Task<RegionContextCaptureResult> CaptureAsync(
        TimeSpan contextLifetime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ForegroundWindowIdentity? foreground = _foreground.GetCurrent();
        if (foreground is null || foreground.WindowHandle == nint.Zero)
        {
            return Failed("The foreground application could not be identified.");
        }

        TargetIdentity target = new(
            string.IsNullOrWhiteSpace(foreground.ProcessName)
                ? $"process-{foreground.ProcessId}"
                : foreground.ProcessName,
            foreground.WindowHandle.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture));

        var completion = new TaskCompletionSource<RegionSelection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new RegionSelectionWindow(_screenCapture);
        void OnSelectionCompleted(object? sender, RegionSelection selection) => completion.TrySetResult(selection);
        window.SelectionCompleted += OnSelectionCompleted;
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            _ = window.DispatcherQueue.TryEnqueue(window.CancelSelection);
        });

        try
        {
            await window.BeginSelectionAsync();
            RegionSelection selection = await completion.Task;
            if (selection.Kind == RegionSelectionKind.Cancelled)
            {
                return new RegionContextCaptureResult(
                    RegionContextCaptureStatus.Cancelled,
                    null,
                    null,
                    selection.Anchor,
                    "Region selection was cancelled.");
            }

            DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
            if (selection.Kind == RegionSelectionKind.TooSmall)
            {
                return Failed("Drag over a larger image or text region to analyze it.");
            }

            if (selection.Region is not { } region)
            {
                return Failed("The selected screen region was invalid.");
            }

            CapturedScreenImage image = _screenCapture.Capture(region);
            return new RegionContextCaptureResult(
                RegionContextCaptureStatus.ImageCaptured,
                CreateInput(image, target, capturedAt, contextLifetime),
                null,
                selection.Anchor,
                string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new RegionContextCaptureResult(
                RegionContextCaptureStatus.Cancelled,
                null,
                null,
                default,
                "Region selection was cancelled.");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Failed("The selected screen region could not be captured safely.");
        }
        finally
        {
            window.CancelSelection();
            window.SelectionCompleted -= OnSelectionCompleted;
        }
    }

    private static ContextExecutionInput CreateInput(
        CapturedScreenImage image,
        TargetIdentity target,
        DateTimeOffset capturedAt,
        TimeSpan contextLifetime)
    {
        byte[] digest = SHA256.HashData(image.EncodedBytes);
        ContextSnapshot context = ContextSnapshot.FromImageDigest(
            ContextSource.RegionCapture,
            target,
            digest,
            capturedAt,
            contextLifetime);
        return ContextExecutionInput.FromImage(
            context,
            new ContextImagePayload(
                image.EncodedBytes,
                image.MediaType,
                image.PixelWidth,
                image.PixelHeight));
    }

    private static RegionContextCaptureResult Failed(string safeDetail) => new(
        RegionContextCaptureStatus.Failed,
        null,
        null,
        default,
        safeDetail);
}
