using Cursivis.Contracts.OpenAI;

namespace Cursivis.Application.OpenAI;

/// <summary>
/// Bounded retry policy for idempotent model reads. Product side effects occur
/// only after the final successful response has been validated.
/// </summary>
public sealed class OpenAiRetryPolicy
{
    public const int MaximumAttempts = 3;

    public bool ShouldRetry(OpenAiFailure? failure, int completedAttempts)
    {
        if (completedAttempts >= MaximumAttempts || failure is not { Retryable: true })
        {
            return false;
        }

        return failure.Kind is
            OpenAiFailureKind.Network or
            OpenAiFailureKind.Timeout or
            OpenAiFailureKind.RateLimit;
    }

    public TimeSpan GetDelay(string? operationId, int completedAttempts)
    {
        if (completedAttempts is < 1 or >= MaximumAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAttempts));
        }

        int stableJitter = string.IsNullOrWhiteSpace(operationId)
            ? 31 * completedAttempts
            : (int)((uint)StringComparer.Ordinal.GetHashCode(operationId) % 91u);
        return TimeSpan.FromMilliseconds(150 + (completedAttempts * 140) + stableJitter);
    }
}
