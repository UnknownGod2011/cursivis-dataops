using System.Security.Cryptography;
using Cursivis.Application.Context;
using Cursivis.Application.OpenAI;
using Cursivis.Application.Realtime;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Context;
using Cursivis.Domain.Models;
using Cursivis.Domain.Settings;

namespace Cursivis.UnitTests;

public sealed class NavigationGuidanceServiceTests
{
    [Fact]
    public async Task GuideAsync_ChangedScreenThenCompletion_UsesVisibleVerifiedSteps()
    {
        var gateway = new StubGateway(
            Decision(false, "Choose Privacy", "The Privacy page opens", "none"),
            Decision(true, string.Empty, string.Empty, "none"));
        var capture = new StubCapture(Frame(1), Frame(2));
        var interaction = new StubInteraction();
        var service = new NavigationGuidanceService(
            gateway,
            capture,
            interaction,
            ModelCatalog.Balanced,
            stabilizationDelay: TimeSpan.Zero);

        NavigationGuidanceSessionResult result = await service.GuideAsync(
            "Open the Privacy page",
            CaptureScope.ActiveWindow);

        Assert.True(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Equal(1, result.CompletedSteps);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.All(gateway.Requests, request => Assert.NotNull(request.Image));
        Assert.Contains(interaction.Snapshots, snapshot => snapshot.State == NavigationGuidanceState.WaitingForUser);
        Assert.Contains(interaction.Snapshots, snapshot => snapshot.State == NavigationGuidanceState.Verifying);
        Assert.Equal(NavigationGuidanceState.Completed, service.Snapshot.State);
    }

    [Fact]
    public async Task GuideAsync_UnchangedScreenThreeTimes_StopsWithoutRepeatedModelCalls()
    {
        var gateway = new StubGateway(Decision(false, "Open Settings", "Settings opens", "none"));
        NavigationGuidanceCaptureFrame same = Frame(7);
        var capture = new StubCapture(same, same, same, same);
        var service = new NavigationGuidanceService(
            gateway,
            capture,
            new StubInteraction(),
            ModelCatalog.Balanced,
            stabilizationDelay: TimeSpan.Zero);

        NavigationGuidanceSessionResult result = await service.GuideAsync(
            "Open Settings",
            CaptureScope.FullDisplay);

        Assert.False(result.Succeeded);
        Assert.Contains("three user actions", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Single(gateway.Requests);
        Assert.Equal(4, capture.CaptureCount);
        Assert.Equal(NavigationGuidanceState.Error, service.Snapshot.State);
    }

    [Fact]
    public async Task GuideAsync_HighImpactBoundary_StopsBeforeUserAction()
    {
        var gateway = new StubGateway(Decision(false, "Submit the form", "The form submits", "stop"));
        var interaction = new StubInteraction();
        var service = new NavigationGuidanceService(
            gateway,
            new StubCapture(Frame(1)),
            interaction,
            ModelCatalog.Balanced,
            stabilizationDelay: TimeSpan.Zero);

        NavigationGuidanceSessionResult result = await service.GuideAsync(
            "Submit this application",
            CaptureScope.ActiveWindow);

        Assert.False(result.Succeeded);
        Assert.Contains("direct user control", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, interaction.WaitCount);
    }

    [Fact]
    public async Task GuideAsync_CancellationWhileWaiting_StopsFurtherCapture()
    {
        var gateway = new StubGateway(Decision(false, "Open menu", "The menu opens", "none"));
        var interaction = new StubInteraction(waitUntilCancelled: true);
        var service = new NavigationGuidanceService(
            gateway,
            new StubCapture(Frame(1)),
            interaction,
            ModelCatalog.Balanced,
            stabilizationDelay: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        NavigationGuidanceSessionResult result = await service.GuideAsync(
            "Open the menu",
            CaptureScope.ActiveWindow,
            cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(NavigationGuidanceState.Cancelled, service.Snapshot.State);
    }

    private static NavigationGuidanceCaptureFrame Frame(byte value)
    {
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10, value];
        byte[] digest = SHA256.HashData(bytes);
        ContextSnapshot context = ContextSnapshot.FromImageDigest(
            ContextSource.RegionCapture,
            new TargetIdentity("fixture", "window-1"),
            digest,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        ContextExecutionInput input = ContextExecutionInput.FromImage(
            context,
            new ContextImagePayload(bytes, "image/png", 1, 1));
        return new NavigationGuidanceCaptureFrame(input, Convert.ToHexString(digest));
    }

    private static string Decision(bool complete, string step, string expected, string boundary) => $$"""
        {
          "schemaVersion": 1,
          "complete": {{complete.ToString().ToLowerInvariant()}},
          "step": "{{step}}",
          "expectedChange": "{{expected}}",
          "boundary": "{{boundary}}"
        }
        """;

    private sealed class StubGateway(params string[] responses) : IResponsesGateway
    {
        private readonly Queue<string> _responses = new(responses);

        public List<StructuredResponseRequest> Requests { get; } = [];

        public Task<StructuredResponseResult> CreateStructuredResponseAsync(
            StructuredResponseRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(StructuredResponseResult.Success(
                _responses.Dequeue(),
                request.Model,
                $"response-{Requests.Count}"));
        }

        public Task<ModelAvailabilityResult> CheckModelAvailabilityAsync(
            string model,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelAvailabilityResult(model, true, null, DateTimeOffset.UtcNow));
    }

    private sealed class StubCapture(params NavigationGuidanceCaptureFrame[] frames)
        : INavigationGuidanceCaptureService
    {
        private readonly Queue<NavigationGuidanceCaptureFrame> _frames = new(frames);

        public int CaptureCount { get; private set; }

        public Task<NavigationGuidanceCaptureFrame> CaptureAsync(
            CaptureScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            return Task.FromResult(_frames.Dequeue());
        }
    }

    private sealed class StubInteraction(bool waitUntilCancelled = false) : INavigationGuidanceInteraction
    {
        public List<NavigationGuidanceSnapshot> Snapshots { get; } = [];

        public int WaitCount { get; private set; }

        public void Show(NavigationGuidanceSnapshot snapshot) => Snapshots.Add(snapshot);

        public async Task WaitForUserActionAsync(CancellationToken cancellationToken = default)
        {
            WaitCount++;
            if (waitUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
    }
}
