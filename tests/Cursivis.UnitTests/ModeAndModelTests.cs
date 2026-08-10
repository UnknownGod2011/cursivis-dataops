using Cursivis.Domain.Actions;
using Cursivis.Domain.Context;
using Cursivis.Domain.Interaction;
using Cursivis.Domain.Models;

namespace Cursivis.UnitTests;

public sealed class ModeAndModelTests
{
    [Fact]
    public void SmartResult_AcceptsStrictTypedResultAndRedactsContentInDiagnostics()
    {
        var result = new SmartResult(
            ContextKind.Code,
            SmartIntent.Debug,
            0.91,
            "customer@example.com is the sensitive result",
            new SuggestedAction(SuggestedActionType.ReplaceSelection, "Replace the selected code"),
            RiskLevel.Low,
            ConfirmationHint.None);

        Assert.Equal(SmartResult.CurrentSchemaVersion, result.SchemaVersion);
        Assert.DoesNotContain("customer@example.com", result.ToString(), StringComparison.Ordinal);
        Assert.Contains("Content=<redacted>", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Replace the selected code", result.SuggestedAction.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(1.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void SmartResult_RejectsInvalidConfidence(double confidence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSmartResult(confidence, "Useful result"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SmartResult_RejectsEmptyFinalContent(string content)
    {
        Assert.Throws<ArgumentException>(() => CreateSmartResult(0.5, content));
    }

    [Theory]
    [InlineData(ContextKind.Text)]
    [InlineData(ContextKind.Code)]
    [InlineData(ContextKind.Image)]
    public void GuidedCatalog_AlwaysOffersCustomTaskAndUsesDeterministicContextOptions(ContextKind kind)
    {
        var operations = GuidedOperationCatalog.For(kind);

        Assert.Contains(GuidedOperation.CustomTask, operations);
        Assert.Equal(operations.Length, operations.Distinct().Count());
    }

    [Fact]
    public void ModelCatalog_ContainsExactRequestedIdentifiers()
    {
        Assert.Equal("gemini-2.5-flash", ModelCatalog.Economy.Value);
        Assert.Equal("gemini-2.5-flash", ModelCatalog.Balanced.Value);
        Assert.Equal("gemini-2.5-pro", ModelCatalog.BestQuality.Value);
        Assert.Equal("gpt-realtime-2.1", ModelCatalog.Realtime.Value);
        Assert.Equal("gpt-realtime-2.1-mini", ModelCatalog.EconomyRealtime.Value);
        Assert.Equal("gpt-realtime-whisper", ModelCatalog.StreamingTranscription.Value);
        Assert.Equal("gpt-4o-mini-transcribe", ModelCatalog.EconomyTranscription.Value);
        Assert.Equal("gpt-4o-transcribe", ModelCatalog.QualityTranscription.Value);
        Assert.Equal(8, ModelCatalog.All.Length);
    }

    [Fact]
    public void RealtimeModels_DoNotClaimStructuredOutputs()
    {
        var realtimeModels = ModelCatalog.All.Where(model => model.Role == ModelRole.Realtime);

        Assert.All(realtimeModels, model =>
        {
            Assert.True(model.Capabilities.HasFlag(ModelCapabilities.FunctionCalling));
            Assert.True(model.Capabilities.HasFlag(ModelCapabilities.AudioInput));
            Assert.False(model.Capabilities.HasFlag(ModelCapabilities.StructuredOutputs));
        });
    }

    [Theory]
    [InlineData(QualityProfile.Economy, "gemini-2.5-flash")]
    [InlineData(QualityProfile.Balanced, "gemini-2.5-flash")]
    [InlineData(QualityProfile.BestQuality, "gemini-2.5-pro")]
    public void ForProfile_MapsQualityWithoutSilentCostUpgrade(QualityProfile profile, string expectedId)
    {
        Assert.Equal(expectedId, ModelCatalog.ForProfile(profile).Id.Value);
    }

    private static SmartResult CreateSmartResult(double confidence, string content) => new(
        ContextKind.Text,
        SmartIntent.Rewrite,
        confidence,
        content,
        SuggestedAction.None,
        RiskLevel.None,
        ConfirmationHint.None);
}
