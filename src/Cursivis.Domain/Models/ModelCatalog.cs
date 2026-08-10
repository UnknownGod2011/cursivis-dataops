using System.Collections.Immutable;

namespace Cursivis.Domain.Models;

[Flags]
public enum ModelCapabilities
{
    None = 0,
    TextInput = 1 << 0,
    TextOutput = 1 << 1,
    ImageInput = 1 << 2,
    AudioInput = 1 << 3,
    AudioOutput = 1 << 4,
    StructuredOutputs = 1 << 5,
    FunctionCalling = 1 << 6,
    StreamingTranscription = 1 << 7,
    BoundedTranscription = 1 << 8,
}

public enum QualityProfile
{
    Economy,
    Balanced,
    BestQuality,
}

public enum ModelRole
{
    Responses,
    Realtime,
    Transcription,
}

public enum RelativeCostTier
{
    Economy,
    Standard,
    Premium,
}

public readonly record struct ModelIdentifier
{
    public ModelIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A model identifier is required.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record ModelDescriptor(
    ModelIdentifier Id,
    string DisplayName,
    ModelRole Role,
    RelativeCostTier CostTier,
    ModelCapabilities Capabilities,
    QualityProfile? QualityProfile = null,
    bool RequiresRuntimeAvailabilityCheck = true);

/// <summary>
/// Canonical model identifiers used by the product. Availability is still verified at runtime.
/// </summary>
public static class ModelCatalog
{
    // Gemini 2.5 Flash is the default because it supports JSON-schema structured
    // output and is broadly available on the Gemini Developer API. The effective
    // model can be overridden with GEMINI_MODEL without changing this registry.
    public static readonly ModelIdentifier Economy = new("gemini-2.5-flash");
    public static readonly ModelIdentifier Balanced = new("gemini-2.5-flash");
    public static readonly ModelIdentifier BestQuality = new("gemini-2.5-pro");
    public static readonly ModelIdentifier Realtime = new("gpt-realtime-2.1");
    public static readonly ModelIdentifier EconomyRealtime = new("gpt-realtime-2.1-mini");
    public static readonly ModelIdentifier StreamingTranscription = new("gpt-realtime-whisper");
    public static readonly ModelIdentifier EconomyTranscription = new("gpt-4o-mini-transcribe");
    public static readonly ModelIdentifier QualityTranscription = new("gpt-4o-transcribe");

    private static readonly ImmutableArray<ModelDescriptor> Catalog =
    [
        new(Economy, "Economy", ModelRole.Responses, RelativeCostTier.Economy,
            ModelCapabilities.TextInput | ModelCapabilities.TextOutput | ModelCapabilities.ImageInput |
            ModelCapabilities.StructuredOutputs | ModelCapabilities.FunctionCalling, QualityProfile.Economy),
        new(Balanced, "Balanced", ModelRole.Responses, RelativeCostTier.Standard,
            ModelCapabilities.TextInput | ModelCapabilities.TextOutput | ModelCapabilities.ImageInput |
            ModelCapabilities.StructuredOutputs | ModelCapabilities.FunctionCalling, QualityProfile.Balanced),
        new(BestQuality, "Best Quality", ModelRole.Responses, RelativeCostTier.Premium,
            ModelCapabilities.TextInput | ModelCapabilities.TextOutput | ModelCapabilities.ImageInput |
            ModelCapabilities.StructuredOutputs | ModelCapabilities.FunctionCalling, QualityProfile.BestQuality),
        new(Realtime, "Live", ModelRole.Realtime, RelativeCostTier.Standard,
            ModelCapabilities.TextInput | ModelCapabilities.TextOutput | ModelCapabilities.ImageInput |
            ModelCapabilities.AudioInput | ModelCapabilities.AudioOutput | ModelCapabilities.FunctionCalling),
        new(EconomyRealtime, "Economy Live", ModelRole.Realtime, RelativeCostTier.Economy,
            ModelCapabilities.TextInput | ModelCapabilities.TextOutput | ModelCapabilities.ImageInput |
            ModelCapabilities.AudioInput | ModelCapabilities.AudioOutput | ModelCapabilities.FunctionCalling),
        new(StreamingTranscription, "Streaming transcription", ModelRole.Transcription, RelativeCostTier.Standard,
            ModelCapabilities.AudioInput | ModelCapabilities.TextOutput | ModelCapabilities.StreamingTranscription),
        new(EconomyTranscription, "Economy transcription", ModelRole.Transcription, RelativeCostTier.Economy,
            ModelCapabilities.AudioInput | ModelCapabilities.TextOutput | ModelCapabilities.BoundedTranscription),
        new(QualityTranscription, "Quality transcription", ModelRole.Transcription, RelativeCostTier.Standard,
            ModelCapabilities.AudioInput | ModelCapabilities.TextOutput | ModelCapabilities.BoundedTranscription),
    ];

    public static ImmutableArray<ModelDescriptor> All => Catalog;

    public static ModelDescriptor ForProfile(QualityProfile profile) =>
        Catalog.Single(model => model.QualityProfile == profile);

    public static ModelDescriptor? Find(ModelIdentifier id) =>
        Catalog.FirstOrDefault(model => model.Id == id);
}
