using Cursivis.Domain.Interaction;

namespace Cursivis.UnitTests;

public sealed class InteractionReducerTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reduce_HappyPath_TraversesAuthoritativeStatesAndResets()
    {
        var operation = new OperationId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var snapshot = InteractionSnapshot.Initial(Start);

        snapshot = Apply(snapshot, new CaptureRequested(operation, Start.AddSeconds(1)));
        Assert.Equal(InteractionState.Capturing, snapshot.State);

        snapshot = Apply(snapshot, new ContextCaptured(operation, Start.AddSeconds(2)));
        Assert.Equal(InteractionState.Planning, snapshot.State);

        snapshot = Apply(snapshot, new ExecutionAuthorized(operation, Start.AddSeconds(3)));
        Assert.Equal(InteractionState.Executing, snapshot.State);

        snapshot = Apply(snapshot, new ExecutionFinished(operation, Start.AddSeconds(4)));
        Assert.Equal(InteractionState.Verifying, snapshot.State);

        snapshot = Apply(snapshot, new VerificationPassed(operation, Start.AddSeconds(5)));
        Assert.Equal(InteractionState.Completed, snapshot.State);
        Assert.Equal(operation, snapshot.ActiveOperation);

        snapshot = Apply(snapshot, new InteractionReset(Start.AddSeconds(6)));
        Assert.Equal(InteractionState.Idle, snapshot.State);
        Assert.Null(snapshot.ActiveOperation);
    }

    [Theory]
    [InlineData(InteractionState.Capturing)]
    [InlineData(InteractionState.Planning)]
    [InlineData(InteractionState.Executing)]
    [InlineData(InteractionState.Verifying)]
    public void Reduce_ActiveState_CanFail(InteractionState state)
    {
        var operation = OperationId.New();
        var snapshot = SnapshotAt(state, operation);

        var result = InteractionReducer.Reduce(
            snapshot,
            new OperationFailed(operation, "provider.timeout", Start.AddMinutes(1)));

        Assert.Equal(InteractionState.Failed, result.Snapshot.State);
        Assert.Equal("provider.timeout", result.Snapshot.FailureCode);
    }

    [Theory]
    [InlineData(InteractionState.Capturing)]
    [InlineData(InteractionState.Planning)]
    [InlineData(InteractionState.Executing)]
    [InlineData(InteractionState.Verifying)]
    public void Reduce_ActiveState_CanCancel(InteractionState state)
    {
        var operation = OperationId.New();
        var result = InteractionReducer.Reduce(
            SnapshotAt(state, operation),
            new OperationCancelled(operation, Start.AddMinutes(1)));

        Assert.Equal(InteractionState.Cancelled, result.Snapshot.State);
        Assert.Null(result.Snapshot.FailureCode);
    }

    [Fact]
    public void Reduce_IllegalTransition_ThrowsWithStateAndEvent()
    {
        var operation = OperationId.New();
        var snapshot = SnapshotAt(InteractionState.Capturing, operation);

        var exception = Assert.Throws<InvalidInteractionTransitionException>(() =>
            InteractionReducer.Reduce(
                snapshot,
                new ExecutionFinished(operation, Start.AddMinutes(1))));

        Assert.Equal(InteractionState.Capturing, exception.State);
        Assert.Equal(typeof(ExecutionFinished), exception.EventType);
    }

    [Fact]
    public void Reduce_EventFromDifferentOperation_IsIgnored()
    {
        var active = OperationId.New();
        var snapshot = SnapshotAt(InteractionState.Planning, active);

        var result = InteractionReducer.Reduce(
            snapshot,
            new ExecutionAuthorized(OperationId.New(), Start.AddMinutes(1)));

        Assert.Equal(InteractionEventDisposition.IgnoredStaleOperation, result.Disposition);
        Assert.Same(snapshot, result.Snapshot);
    }

    [Fact]
    public void Reduce_EventOlderThanSnapshot_IsIgnoredEvenForSameOperation()
    {
        var operation = OperationId.New();
        var snapshot = new InteractionSnapshot(
            InteractionState.Planning,
            operation,
            Start.AddMinutes(2));

        var result = InteractionReducer.Reduce(
            snapshot,
            new ExecutionAuthorized(operation, Start.AddMinutes(1)));

        Assert.Equal(InteractionEventDisposition.IgnoredStaleOperation, result.Disposition);
        Assert.Same(snapshot, result.Snapshot);
    }

    [Fact]
    public void Reduce_CannotStartSecondOperationWhileActive()
    {
        var snapshot = SnapshotAt(InteractionState.Capturing, OperationId.New());

        Assert.Throws<InvalidInteractionTransitionException>(() =>
            InteractionReducer.Reduce(
                snapshot,
                new CaptureRequested(OperationId.New(), Start.AddMinutes(1))));
    }

    [Fact]
    public void OperationId_RejectsEmptyIdentifier()
    {
        Assert.Throws<ArgumentException>(() => new OperationId(Guid.Empty));
    }

    private static InteractionSnapshot Apply(InteractionSnapshot snapshot, InteractionEvent @event) =>
        InteractionReducer.Reduce(snapshot, @event).Snapshot;

    private static InteractionSnapshot SnapshotAt(InteractionState state, OperationId operation) =>
        new(state, operation, Start);
}
