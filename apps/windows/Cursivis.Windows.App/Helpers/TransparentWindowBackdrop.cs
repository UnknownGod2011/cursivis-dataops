using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.UI.Composition;
using WinRT;

namespace Cursivis.Windows.App.Helpers;

/// <summary>
/// Gives a WinUI desktop overlay a genuinely transparent compositor backdrop.
/// Transparent XAML alone otherwise reveals the swapchain's opaque fallback.
/// </summary>
internal sealed class TransparentWindowBackdrop : IDisposable
{
    private readonly ICompositionSupportsSystemBackdrop _target;
    private readonly CompositionColorBrush _brush;
    private bool _disposed;

    public TransparentWindowBackdrop(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _target = window.As<ICompositionSupportsSystemBackdrop>();
        // SystemBackdrop accepts a transparent Windows composition brush;
        // do not enable DWM blur-behind here because it paints a translucent
        // rectangle over otherwise transparent overlay pixels.
        _brush = new Compositor().CreateColorBrush(
            global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
        _target.SystemBackdrop = _brush;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _target.SystemBackdrop = null;
        _brush.Dispose();
    }
}
