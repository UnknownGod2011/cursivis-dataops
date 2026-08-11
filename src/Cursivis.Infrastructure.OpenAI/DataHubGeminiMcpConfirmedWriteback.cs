namespace Cursivis.Infrastructure.OpenAI;

public sealed partial class DataHubGeminiResponsesGateway
{
    /// <summary>
    /// Persists only content that can be sent to the existing MCP write-back path
    /// without normalization. This keeps the explicit UI confirmation bound to the
    /// exact reviewed artifact instead of silently trimming it before save_document.
    /// </summary>
    public Task<DataHubResolutionSaveResult> SaveConfirmedResolutionAsync(
        string resolutionText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resolutionText))
        {
            return Task.FromResult(
                DataHubResolutionSaveResult.Failed("There is no reviewed resolution to save."));
        }

        if (!string.Equals(resolutionText, resolutionText.Trim(), StringComparison.Ordinal))
        {
            return Task.FromResult(
                DataHubResolutionSaveResult.Failed(
                    "The reviewed resolution contains leading or trailing whitespace. Cursivis will not modify confirmed content before DataHub MCP write-back; remove the extra whitespace, review the result again, and confirm the save."));
        }

        return SaveResolutionAsync(resolutionText, cancellationToken);
    }
}
