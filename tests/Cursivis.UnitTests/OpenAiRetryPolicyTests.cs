using Cursivis.Application.OpenAI;
using Cursivis.Contracts.OpenAI;

namespace Cursivis.UnitTests;

public sealed class OpenAiRetryPolicyTests
{
    private readonly OpenAiRetryPolicy _policy = new();

    [Theory]
    [InlineData(OpenAiFailureKind.Network)]
    [InlineData(OpenAiFailureKind.Timeout)]
    [InlineData(OpenAiFailureKind.RateLimit)]
    public void ShouldRetry_BoundedTransientFailure_Retries(OpenAiFailureKind kind)
    {
        var failure = new OpenAiFailure(kind, "Temporary failure.", true);

        Assert.True(_policy.ShouldRetry(failure, completedAttempts: 1));
        Assert.True(_policy.ShouldRetry(failure, completedAttempts: 2));
        Assert.False(_policy.ShouldRetry(failure, completedAttempts: 3));
    }

    [Theory]
    [InlineData(OpenAiFailureKind.Authentication)]
    [InlineData(OpenAiFailureKind.Quota)]
    [InlineData(OpenAiFailureKind.MalformedResponse)]
    [InlineData(OpenAiFailureKind.Cancelled)]
    public void ShouldRetry_PermanentFailure_DoesNotRetry(OpenAiFailureKind kind)
    {
        var failure = new OpenAiFailure(kind, "Permanent failure.", false);

        Assert.False(_policy.ShouldRetry(failure, completedAttempts: 1));
    }

    [Fact]
    public void GetDelay_IsShortBoundedAndStableForOperation()
    {
        TimeSpan first = _policy.GetDelay("operation-42", completedAttempts: 1);
        TimeSpan repeated = _policy.GetDelay("operation-42", completedAttempts: 1);
        TimeSpan second = _policy.GetDelay("operation-42", completedAttempts: 2);

        Assert.Equal(first, repeated);
        Assert.InRange(first.TotalMilliseconds, 290, 380);
        Assert.InRange(second.TotalMilliseconds, 430, 520);
    }
}
