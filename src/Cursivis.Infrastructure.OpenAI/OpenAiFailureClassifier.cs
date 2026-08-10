using System.ClientModel;
using System.Net;
using Cursivis.Contracts.OpenAI;

namespace Cursivis.Infrastructure.OpenAI;

internal static class OpenAiFailureClassifier
{
    public static OpenAiFailure Classify(ClientResultException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Status switch
        {
            (int)HttpStatusCode.Unauthorized => new(
                OpenAiFailureKind.Authentication,
                "OpenAI rejected the configured API key.",
                false),
            (int)HttpStatusCode.Forbidden => new(
                OpenAiFailureKind.Permission,
                "The OpenAI project does not permit this request or model.",
                false),
            (int)HttpStatusCode.NotFound => new(
                OpenAiFailureKind.ModelUnavailable,
                "The selected OpenAI model is not available to this project.",
                false),
            (int)HttpStatusCode.RequestTimeout => new(
                OpenAiFailureKind.Timeout,
                "The OpenAI request timed out.",
                true),
            (int)HttpStatusCode.TooManyRequests when IsQuotaFailure(exception) => new(
                OpenAiFailureKind.Quota,
                "OpenAI quota or credits may be exhausted.",
                false),
            (int)HttpStatusCode.TooManyRequests => new(
                OpenAiFailureKind.RateLimit,
                "OpenAI is temporarily rate limiting this request.",
                true),
            >= 500 => new(
                OpenAiFailureKind.Network,
                "OpenAI is temporarily unavailable.",
                true),
            _ => new(
                OpenAiFailureKind.Unknown,
                "The OpenAI request failed.",
                false),
        };
    }

    private static bool IsQuotaFailure(ClientResultException exception)
    {
        string message = exception.Message ?? string.Empty;
        return message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("credit", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("billing", StringComparison.OrdinalIgnoreCase);
    }
}
