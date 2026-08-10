using Cursivis.Domain.Models;

namespace Cursivis.Infrastructure.OpenAI;

public sealed record OpenAiModelDescriptor(
    ModelDescriptor Model,
    bool AccountAccessMayBeRestricted,
    IReadOnlyList<ModelIdentifier> AllowedFallbacks)
{
    public string Id => Model.Id.Value;
}

/// <summary>
/// Adds adapter-specific fallback and account-access metadata to the canonical domain model catalog.
/// </summary>
public static class CursivisModelCatalog
{
    public static string Economy => ModelCatalog.Economy.Value;
    public static string Balanced => ModelCatalog.Balanced.Value;
    public static string BestQuality => ModelCatalog.BestQuality.Value;
    public static string Live => ModelCatalog.Realtime.Value;
    public static string EconomyLive => ModelCatalog.EconomyRealtime.Value;
    public static string StreamingTranscription => ModelCatalog.StreamingTranscription.Value;
    public static string EconomyTranscription => ModelCatalog.EconomyTranscription.Value;
    public static string QualityTranscription => ModelCatalog.QualityTranscription.Value;

    public static IReadOnlyList<OpenAiModelDescriptor> All { get; } = ModelCatalog.All
        .Select(model => new OpenAiModelDescriptor(
            model,
            model.Id == ModelCatalog.EconomyRealtime,
            GetFallbacks(model.Id)))
        .ToArray();

    private static readonly IReadOnlyDictionary<string, OpenAiModelDescriptor> ById =
        All.ToDictionary(model => model.Id, StringComparer.Ordinal);

    public static OpenAiModelDescriptor GetRequired(string id) =>
        ById.TryGetValue(id, out OpenAiModelDescriptor? descriptor)
            ? descriptor
            : throw new ArgumentException("The model is not registered for this Cursivis build.", nameof(id));

    public static bool Supports(string id, ModelCapabilities capabilities) =>
        ById.TryGetValue(id, out OpenAiModelDescriptor? descriptor)
        && (descriptor.Model.Capabilities & capabilities) == capabilities;

    private static IReadOnlyList<ModelIdentifier> GetFallbacks(ModelIdentifier id)
    {
        if (id == ModelCatalog.BestQuality)
        {
            return [ModelCatalog.Balanced, ModelCatalog.Economy];
        }

        if (id == ModelCatalog.Balanced)
        {
            return [ModelCatalog.Economy];
        }

        if (id == ModelCatalog.StreamingTranscription || id == ModelCatalog.QualityTranscription)
        {
            return [ModelCatalog.EconomyTranscription];
        }

        return [];
    }
}
