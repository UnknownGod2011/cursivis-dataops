namespace Cursivis.Infrastructure.BrowserBridge;

public static class BrowserBridgeLimits
{
    public const int MaximumFrameBytes = 1_048_576;
    public const int MaximumSelectionCharacters = 50_000;
    public const int MaximumNearbyContextCharacters = 8_000;
    public const int MaximumFormFields = 300;
    public const int MaximumPlanSteps = 20;
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaximumHandshakeLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);
}
