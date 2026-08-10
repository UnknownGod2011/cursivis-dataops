using Cursivis.Application.Context;
using Cursivis.Domain.Context;
using Cursivis.Windows.Platform.Foreground;
using Cursivis.Windows.Platform.Selection;

namespace Cursivis.IntegrationTests.Windows;

public sealed class LayeredSelectionCaptureServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CaptureAsync_UiAutomationFindsText_DoesNotInvokeClipboardFallback()
    {
        var fallback = new StubReader(TextSelectionReadResult.Failed(
            TextSelectionReadStatus.NoSelection,
            ContextSource.ProtectedClipboard,
            "No clipboard text."));
        var service = CreateService(
            new StubReader(TextSelectionReadResult.Captured(" selected text ", ContextSource.UserInterfaceAutomation)),
            fallback);

        SelectionCaptureResult result = await service.CaptureAsync(
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(SelectionCaptureStatus.Captured, result.Status);
        Assert.Equal("selected text", result.Context?.Text);
        Assert.Equal(ContextSource.UserInterfaceAutomation, result.Context?.Source);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task CaptureAsync_UiAutomationHasNoSelection_UsesClipboardFallback()
    {
        var service = CreateService(
            new StubReader(TextSelectionReadResult.Failed(
                TextSelectionReadStatus.NoSelection,
                ContextSource.UserInterfaceAutomation,
                "No UIA selection.")),
            new StubReader(TextSelectionReadResult.Captured("fallback", ContextSource.ProtectedClipboard)));

        SelectionCaptureResult result = await service.CaptureAsync(
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(SelectionCaptureStatus.Captured, result.Status);
        Assert.Equal(ContextSource.ProtectedClipboard, result.Context?.Source);
    }

    [Fact]
    public async Task CaptureAsync_ChromiumUiAutomationUrl_UsesProtectedClipboardSelection()
    {
        var clipboard = new BoundStubReader(TextSelectionReadResult.Captured(
            "extempore",
            ContextSource.ProtectedClipboard));
        var service = new LayeredSelectionCaptureService(
            new SequencedForegroundProvider(
                new ForegroundWindowIdentity(7, "chrome", "Google", new nint(1))),
            [
                new StubReader(TextSelectionReadResult.Captured(
                    "https://www.google.com/search?q=extempore",
                    ContextSource.UserInterfaceAutomation)),
                clipboard,
            ],
            new FixedTimeProvider(Now));

        SelectionCaptureResult result = await service.CaptureAsync(
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(SelectionCaptureStatus.Captured, result.Status);
        Assert.Equal("extempore", result.Context?.Text);
        Assert.Equal(ContextSource.ProtectedClipboard, result.Context?.Source);
        Assert.Equal(new nint(1), clipboard.SourceWindowHandle);
    }

    [Fact]
    public async Task CaptureAsync_ChromiumFocusedPageText_KeepsUiAutomationSelection()
    {
        var fallback = new BoundStubReader(TextSelectionReadResult.Captured(
            "should-not-be-used",
            ContextSource.ProtectedClipboard));
        var service = new LayeredSelectionCaptureService(
            new SequencedForegroundProvider(
                new ForegroundWindowIdentity(7, "chrome", "Article", new nint(1))),
            [
                new StubReader(TextSelectionReadResult.Captured(
                    "extempore speech is delivered without preparation",
                    ContextSource.UserInterfaceAutomation)),
                fallback,
            ],
            new FixedTimeProvider(Now));

        SelectionCaptureResult result = await service.CaptureAsync(
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(SelectionCaptureStatus.Captured, result.Status);
        Assert.Equal("extempore speech is delivered without preparation", result.Context?.Text);
        Assert.Equal(ContextSource.UserInterfaceAutomation, result.Context?.Source);
        Assert.Equal(nint.Zero, fallback.SourceWindowHandle);
    }

    [Fact]
    public async Task CaptureAsync_OrdinaryProseWithDomainLikeText_DoesNotTreatAsBrowserUrl()
    {
        var fallback = new BoundStubReader(TextSelectionReadResult.Captured(
            "should-not-be-used",
            ContextSource.ProtectedClipboard));
        var service = new LayeredSelectionCaptureService(
            new SequencedForegroundProvider(
                new ForegroundWindowIdentity(7, "chrome", "Docs", new nint(1))),
            [
                new StubReader(TextSelectionReadResult.Captured(
                    "See example.com for details, please.",
                    ContextSource.UserInterfaceAutomation)),
                fallback,
            ],
            new FixedTimeProvider(Now));

        SelectionCaptureResult result = await service.CaptureAsync(
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(SelectionCaptureStatus.Captured, result.Status);
        Assert.Equal("See example.com for details, please.", result.Context?.Text);
        Assert.Equal(ContextSource.UserInterfaceAutomation, result.Context?.Source);
        Assert.Equal(nint.Zero, fallback.SourceWindowHandle);
    }

    [Fact]
    public async Task CaptureAsync_ChromiumHttpsUrlWithoutClipboard_ReturnsNoSelection()
    {
        var service = new LayeredSelectionCaptureService(
            new SequencedForegroundProvider(
                new ForegroundWindowIdentity(7, "msedge", "Search", new nint(1))),
            [
                new StubReader(TextSelectionReadResult.Captured(
                    "https://www.bing.com/search?q=hello",
                    ContextSource.UserInterfaceAutomation)),
                new BoundStubReader(TextSelectionReadResult.Failed(
                    TextSelectionReadStatus.NoSelection,
                    ContextSource.ProtectedClipboard,
                    "No clipboard selection.")),
            ],
            new FixedTimeProvider(Now));

        SelectionCaptureResult result = await service.CaptureAsync(
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(SelectionCaptureStatus.NoSelection, result.Status);
        Assert.Null(result.Context);
    }

    [Fact]
    public async Task CaptureAsync_ForegroundChanges_RejectsCapturedText()
    {
        var foreground = new SequencedForegroundProvider(
            new ForegroundWindowIdentity(7, "notepad", "Draft", new nint(1)),
            new ForegroundWindowIdentity(8, "browser", "Other", new nint(2)));
        var service = new LayeredSelectionCaptureService(
            foreground,
            [new StubReader(TextSelectionReadResult.Captured("text", ContextSource.UserInterfaceAutomation))],
            new FixedTimeProvider(Now));

        SelectionCaptureResult result = await service.CaptureAsync(
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(SelectionCaptureStatus.ForegroundChanged, result.Status);
        Assert.Null(result.Context);
    }

    [Fact]
    public async Task CaptureAsync_NoReaderFindsText_ReturnsNoSelection()
    {
        TextSelectionReadResult none = TextSelectionReadResult.Failed(
            TextSelectionReadStatus.NoSelection,
            ContextSource.UserInterfaceAutomation,
            "No selection.");
        var service = CreateService(new StubReader(none), new StubReader(none));

        SelectionCaptureResult result = await service.CaptureAsync(
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(SelectionCaptureStatus.NoSelection, result.Status);
    }

    [Fact]
    public async Task CaptureAsync_ForegroundBoundFallback_ReceivesOriginalSourceWindow()
    {
        var fallback = new BoundStubReader(TextSelectionReadResult.Captured(
            "clipboard fallback",
            ContextSource.ProtectedClipboard));
        var service = CreateService(
            new StubReader(TextSelectionReadResult.Failed(
                TextSelectionReadStatus.NoSelection,
                ContextSource.UserInterfaceAutomation,
                "No UIA selection.")),
            fallback);

        SelectionCaptureResult result = await service.CaptureAsync(
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(SelectionCaptureStatus.Captured, result.Status);
        Assert.Equal(new nint(1), fallback.SourceWindowHandle);
    }

    private static LayeredSelectionCaptureService CreateService(params ITextSelectionReader[] readers) =>
        new(
            new SequencedForegroundProvider(
                new ForegroundWindowIdentity(7, "notepad", "Draft", new nint(1))),
            readers,
            new FixedTimeProvider(Now));

    private sealed class StubReader(TextSelectionReadResult result) : ITextSelectionReader
    {
        public int CallCount { get; private set; }

        public Task<TextSelectionReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class BoundStubReader(TextSelectionReadResult result)
        : IForegroundBoundTextSelectionReader
    {
        public nint SourceWindowHandle { get; private set; }

        public Task<TextSelectionReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public Task<TextSelectionReadResult> ReadForWindowAsync(
            nint sourceWindowHandle,
            CancellationToken cancellationToken = default)
        {
            SourceWindowHandle = sourceWindowHandle;
            return Task.FromResult(result);
        }
    }

    private sealed class SequencedForegroundProvider(params ForegroundWindowIdentity[] values)
        : IForegroundWindowIdentityProvider
    {
        private int _index;

        public ForegroundWindowIdentity? GetCurrent()
        {
            int index = Math.Min(_index, values.Length - 1);
            _index++;
            return values[index];
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
