using Cursivis.Application.Context;
using Cursivis.Domain.Actions;
using Cursivis.Domain.Context;
using Cursivis.Domain.Interaction;

namespace Cursivis.UnitTests;

public sealed class ContextInteractionSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetCurrent_AfterResultUpdate_ReusesOriginalContextAndFingerprint()
    {
        var time = new MutableTimeProvider(Now);
        var session = new ContextInteractionSession(time);
        ContextSnapshot context = CreateContext();
        SmartResult result = CreateResult("First result");

        session.Begin(context);
        session.SetResult(context.Fingerprint, result);
        ContextSessionAccess access = session.GetCurrent();

        Assert.Equal(ContextSessionAccessStatus.Available, access.Status);
        Assert.Same(context, access.Context);
        Assert.Same(result, access.LatestResult);
        Assert.Equal(context.Fingerprint, session.Fingerprint);
    }

    [Fact]
    public void SetResult_WithClipboardOverride_PreservesVisibleResultAndCopyValue()
    {
        var session = new ContextInteractionSession(new MutableTimeProvider(Now));
        ContextSnapshot context = CreateContext();
        SmartResult result = CreateResult("#12ABEF\nRGB (18, 171, 239)\nblue");

        session.Begin(context);
        session.SetResult(context.Fingerprint, result, "#12ABEF");
        ContextSessionAccess access = session.GetCurrent();

        Assert.Equal(result.FinalContent, access.LatestResult?.FinalContent);
        Assert.Equal("#12ABEF", access.ClipboardText);
    }

    [Fact]
    public void GetCurrent_AfterExpiry_ClearsSensitiveContext()
    {
        var time = new MutableTimeProvider(Now);
        var session = new ContextInteractionSession(time);
        session.Begin(CreateContext());
        time.Now = Now.AddMinutes(6);

        ContextSessionAccess access = session.GetCurrent();

        Assert.Equal(ContextSessionAccessStatus.Expired, access.Status);
        Assert.Null(access.Context);
        Assert.Null(session.Fingerprint);
        Assert.Equal(ContextSessionAccessStatus.Missing, session.GetCurrent().Status);
    }

    [Fact]
    public async Task CopyFinalOnceAsync_RepeatedCompletion_CopiesExactlyOnce()
    {
        var clipboard = new RecordingClipboardService(ResultClipboardWriteStatus.Copied);
        var coordinator = new ResultAutoCopyCoordinator(clipboard);
        OperationId operation = OperationId.New();
        SmartResult result = CreateResult("Copied once");

        ResultClipboardWriteResult first = await coordinator.CopyFinalOnceAsync(operation, result.FinalContent);
        ResultClipboardWriteResult second = await coordinator.CopyFinalOnceAsync(operation, result.FinalContent);

        Assert.Equal(ResultClipboardWriteStatus.Copied, first.Status);
        Assert.Same(first, second);
        Assert.Equal(1, clipboard.CallCount);
        Assert.Equal("Copied once", clipboard.LastText);
    }

    [Fact]
    public async Task CopyFinalOnceAsync_ConcurrentCompletion_CopiesExactlyOnce()
    {
        var clipboard = new RecordingClipboardService(ResultClipboardWriteStatus.Copied);
        var coordinator = new ResultAutoCopyCoordinator(clipboard);
        OperationId operation = OperationId.New();

        Task<ResultClipboardWriteResult>[] writes = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => coordinator.CopyFinalOnceAsync(operation, "Copied once")))
            .ToArray();
        ResultClipboardWriteResult[] results = await Task.WhenAll(writes);

        Assert.All(results, result => Assert.Equal(ResultClipboardWriteStatus.Copied, result.Status));
        Assert.Equal(1, clipboard.CallCount);
        Assert.Equal("Copied once", clipboard.LastText);
    }

    [Fact]
    public async Task CopyFinalOnceAsync_ClipboardFailure_IsStableAndDoesNotLoseResult()
    {
        var clipboard = new RecordingClipboardService(ResultClipboardWriteStatus.Failed);
        var coordinator = new ResultAutoCopyCoordinator(clipboard);
        SmartResult result = CreateResult("Still visible");

        ResultClipboardWriteResult copy = await coordinator.CopyFinalOnceAsync(
            OperationId.New(),
            result.FinalContent);

        Assert.Equal(ResultClipboardWriteStatus.Failed, copy.Status);
        Assert.Equal("Still visible", result.FinalContent);
        Assert.Equal(1, clipboard.CallCount);
    }

    [Fact]
    public async Task CopyFinalOnceAsync_ColorResult_CopiesHexInsteadOfDisplayText()
    {
        var clipboard = new RecordingClipboardService(ResultClipboardWriteStatus.Copied);
        var coordinator = new ResultAutoCopyCoordinator(clipboard);
        OperationId operation = OperationId.New();

        await coordinator.CopyFinalOnceAsync(operation, "#12ABEF");
        await coordinator.CopyFinalOnceAsync(operation, "#12ABEF");

        Assert.Equal(1, clipboard.CallCount);
        Assert.Equal("#12ABEF", clipboard.LastText);
    }

    private static ContextSnapshot CreateContext() => ContextSnapshot.FromText(
        ContextKind.Text,
        ContextSource.UserInterfaceAutomation,
        new TargetIdentity("notepad", "00000001"),
        "Selected context",
        Now,
        TimeSpan.FromMinutes(5));

    private static SmartResult CreateResult(string content) => new(
        ContextKind.Text,
        SmartIntent.Rewrite,
        0.9,
        content,
        SuggestedAction.None,
        RiskLevel.Low,
        ConfirmationHint.None);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingClipboardService(ResultClipboardWriteStatus status)
        : IResultClipboardService
    {
        private int _callCount;

        public int CallCount => _callCount;

        public string? LastText { get; private set; }

        public Task<ResultClipboardWriteResult> CopyTextAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            LastText = text;
            return Task.FromResult(new ResultClipboardWriteResult(status, status.ToString()));
        }
    }
}
