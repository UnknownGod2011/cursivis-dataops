namespace Cursivis.Domain.Interaction;

public enum InteractionState
{
    Idle,
    Capturing,
    Planning,
    Executing,
    Verifying,
    Completed,
    Failed,
    Cancelled,
}

public readonly record struct OperationId
{
    public OperationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An operation identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static OperationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public sealed record InteractionSnapshot(
    InteractionState State,
    OperationId? ActiveOperation,
    DateTimeOffset UpdatedAt,
    string? FailureCode = null)
{
    public static InteractionSnapshot Initial(DateTimeOffset now) =>
        new(InteractionState.Idle, null, now);
}

public abstract record InteractionEvent(OperationId? Operation, DateTimeOffset OccurredAt);

public sealed record CaptureRequested(OperationId OperationId, DateTimeOffset OccurredAt)
    : InteractionEvent(OperationId, OccurredAt);

public sealed record ContextCaptured(OperationId OperationId, DateTimeOffset OccurredAt)
    : InteractionEvent(OperationId, OccurredAt);

public sealed record ExecutionAuthorized(OperationId OperationId, DateTimeOffset OccurredAt)
    : InteractionEvent(OperationId, OccurredAt);

public sealed record ExecutionFinished(OperationId OperationId, DateTimeOffset OccurredAt)
    : InteractionEvent(OperationId, OccurredAt);

public sealed record VerificationPassed(OperationId OperationId, DateTimeOffset OccurredAt)
    : InteractionEvent(OperationId, OccurredAt);

public sealed record OperationFailed(OperationId OperationId, string FailureCode, DateTimeOffset OccurredAt)
    : InteractionEvent(OperationId, OccurredAt);

public sealed record OperationCancelled(OperationId OperationId, DateTimeOffset OccurredAt)
    : InteractionEvent(OperationId, OccurredAt);

public sealed record InteractionReset(DateTimeOffset OccurredAt)
    : InteractionEvent(null, OccurredAt);

public enum InteractionEventDisposition
{
    Applied,
    IgnoredStaleOperation,
}

public sealed record InteractionReduction(
    InteractionSnapshot Snapshot,
    InteractionEventDisposition Disposition);

public sealed class InvalidInteractionTransitionException : InvalidOperationException
{
    public InvalidInteractionTransitionException(InteractionState state, Type eventType)
        : base($"Event '{eventType.Name}' is not valid while the interaction is '{state}'.")
    {
        State = state;
        EventType = eventType;
    }

    public InteractionState State { get; }

    public Type EventType { get; }
}

/// <summary>
/// The single authoritative interaction reducer. It never performs I/O and ignores stale async completions.
/// </summary>
public static class InteractionReducer
{
    public static InteractionReduction Reduce(InteractionSnapshot current, InteractionEvent @event)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(@event);

        if (@event.OccurredAt < current.UpdatedAt)
        {
            return new(current, InteractionEventDisposition.IgnoredStaleOperation);
        }

        if (@event is CaptureRequested captureRequested)
        {
            return ReduceCaptureRequested(current, captureRequested);
        }

        if (@event is InteractionReset)
        {
            if (!IsTerminal(current.State))
            {
                throw new InvalidInteractionTransitionException(current.State, @event.GetType());
            }

            return new(
                new InteractionSnapshot(InteractionState.Idle, null, @event.OccurredAt),
                InteractionEventDisposition.Applied);
        }

        if (current.ActiveOperation != @event.Operation)
        {
            return new(current, InteractionEventDisposition.IgnoredStaleOperation);
        }

        var next = (current.State, @event) switch
        {
            (InteractionState.Capturing, ContextCaptured) => InteractionState.Planning,
            (InteractionState.Planning, ExecutionAuthorized) => InteractionState.Executing,
            (InteractionState.Executing, ExecutionFinished) => InteractionState.Verifying,
            (InteractionState.Verifying, VerificationPassed) => InteractionState.Completed,
            (_, OperationFailed) when IsActive(current.State) => InteractionState.Failed,
            (_, OperationCancelled) when IsActive(current.State) => InteractionState.Cancelled,
            _ => throw new InvalidInteractionTransitionException(current.State, @event.GetType()),
        };

        var failureCode = @event is OperationFailed failed
            ? RequireFailureCode(failed.FailureCode)
            : null;

        return new(
            new InteractionSnapshot(next, current.ActiveOperation, @event.OccurredAt, failureCode),
            InteractionEventDisposition.Applied);
    }

    private static InteractionReduction ReduceCaptureRequested(
        InteractionSnapshot current,
        CaptureRequested @event)
    {
        if (IsActive(current.State))
        {
            throw new InvalidInteractionTransitionException(current.State, @event.GetType());
        }

        if (current.ActiveOperation == @event.Operation)
        {
            throw new InvalidInteractionTransitionException(current.State, @event.GetType());
        }

        return new(
            new InteractionSnapshot(InteractionState.Capturing, @event.OperationId, @event.OccurredAt),
            InteractionEventDisposition.Applied);
    }

    private static string RequireFailureCode(string failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            throw new ArgumentException("A non-empty failure code is required.", nameof(failureCode));
        }

        return failureCode.Trim();
    }

    private static bool IsActive(InteractionState state) => state is
        InteractionState.Capturing or
        InteractionState.Planning or
        InteractionState.Executing or
        InteractionState.Verifying;

    private static bool IsTerminal(InteractionState state) => state is
        InteractionState.Idle or
        InteractionState.Completed or
        InteractionState.Failed or
        InteractionState.Cancelled;
}
