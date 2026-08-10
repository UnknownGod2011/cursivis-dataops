using Cursivis.Windows.Platform.Instance;

namespace Cursivis.IntegrationTests.Windows;

public sealed class SingleInstanceAndActivationTests
{
    [Fact]
    public void UserScopedNames_AreDeterministicSessionBoundedAndDoNotExposeSid()
    {
        const string applicationId = "OpenAI.Cursivis.Tests";
        const string userSid = "S-1-5-21-123456789-222222222-333333333-1001";
        var first = UserScopedInstanceNames.Create(applicationId, userSid, sessionId: 7);
        var second = UserScopedInstanceNames.Create(applicationId, userSid, sessionId: 7);
        var anotherSession = UserScopedInstanceNames.Create(applicationId, userSid, sessionId: 8);

        Assert.Equal(first, second);
        Assert.NotEqual(first, anotherSession);
        Assert.StartsWith("Local\\", first.MutexName, StringComparison.Ordinal);
        Assert.Contains("s7", first.MutexName, StringComparison.Ordinal);
        Assert.DoesNotContain(userSid, first.MutexName, StringComparison.Ordinal);
        Assert.DoesNotContain(userSid, first.ActivationPipeName, StringComparison.Ordinal);
        Assert.DoesNotContain(':', first.ActivationPipeName);
        Assert.DoesNotContain('/', first.ActivationPipeName);
    }

    [Fact]
    public void Acquire_ReturnsOnePrimaryLeaseUntilAllHandlesClose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var applicationId = $"OpenAI.Cursivis.Tests.{Guid.NewGuid():N}";
        var coordinator = new WindowsSingleInstanceCoordinator(applicationId);

        using (var primary = coordinator.Acquire())
        using (var secondary = coordinator.Acquire())
        {
            Assert.True(primary.IsPrimaryInstance);
            Assert.False(secondary.IsPrimaryInstance);
            Assert.Equal(primary.Names, secondary.Names);
        }

        using var replacementPrimary = coordinator.Acquire();
        Assert.True(replacementPrimary.IsPrimaryInstance);
    }

    [Fact]
    public async Task ActivationHandoff_DeliversBoundedTypedRequestAndAcknowledgesIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var names = UserScopedInstanceNames.ForCurrentUser(
            $"OpenAI.Cursivis.Tests.{Guid.NewGuid():N}");
        var handoff = new CurrentUserActivationHandoff(names.ActivationPipeName);
        var received = new TaskCompletionSource<ActivationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var serverCancellation = new CancellationTokenSource();
        var serverTask = handoff.RunAsync(
            (request, _) =>
            {
                received.TrySetResult(request);
                return ValueTask.CompletedTask;
            },
            serverCancellation.Token);

        var sent = ActivationRequest.Create(ActivationRequestKind.OpenSettings);
        var result = await handoff.SendAsync(sent, TimeSpan.FromSeconds(5));
        var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Delivered);
        Assert.Equal(ActivationHandoffStatus.Delivered, result.Status);
        Assert.Equal(sent, delivered);

        serverCancellation.Cancel();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ActivationHandoff_WhenHandlerFails_ReturnsSanitizedFailureAndServerStopsCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var names = UserScopedInstanceNames.ForCurrentUser(
            $"OpenAI.Cursivis.Tests.{Guid.NewGuid():N}");
        var handoff = new CurrentUserActivationHandoff(names.ActivationPipeName);
        using var serverCancellation = new CancellationTokenSource();
        var serverTask = handoff.RunAsync(
            static (_, _) => throw new InvalidOperationException("Injected handler failure."),
            serverCancellation.Token);

        var result = await handoff.SendAsync(
            ActivationRequest.Create(ActivationRequestKind.ShowOverview),
            TimeSpan.FromSeconds(5));

        Assert.Equal(ActivationHandoffStatus.HandlerFailed, result.Status);
        Assert.False(result.Delivered);
        Assert.DoesNotContain("Injected", result.ToString(), StringComparison.Ordinal);

        serverCancellation.Cancel();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ActivationHandoff_RejectsExpiredRequestWithoutConnecting()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var names = UserScopedInstanceNames.ForCurrentUser(
            $"OpenAI.Cursivis.Tests.{Guid.NewGuid():N}");
        var handoff = new CurrentUserActivationHandoff(names.ActivationPipeName);
        var now = DateTimeOffset.UtcNow;
        var expired = new ActivationRequest(
            ActivationRequest.CurrentSchemaVersion,
            ActivationRequestKind.OpenSettings,
            Guid.NewGuid().ToString("N"),
            now.AddMinutes(-2),
            now.AddMinutes(-1));

        var result = await handoff.SendAsync(expired, TimeSpan.FromSeconds(1));

        Assert.Equal(ActivationHandoffStatus.Rejected, result.Status);
    }
}
